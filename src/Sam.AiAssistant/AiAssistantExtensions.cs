using System.Security.Claims;
using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Sam.AiAssistant;

public static class AiAssistantExtensions
{
    /// <summary>ثبت سرویس‌ها: <c>builder.Services.AddAiAssistant(o => { ... });</c></summary>
    public static IServiceCollection AddAiAssistant(this IServiceCollection services, Action<AiAssistantOptions> configure)
    {
        var options = new AiAssistantOptions();
        configure(options);
        options.Validate();

        services.AddSingleton(options);
        services.AddSingleton<AiAssistantStore>();
        services.AddSingleton<AiConversationMemory>();
        services.AddScoped<AiContextBuilder>();
        services.AddHttpClient<AiChatService>(c => c.Timeout = Timeout.InfiniteTimeSpan); // Timeout از تنظیمات خوانده می‌شود
        services.AddHostedService<AiAssistantInitializer>();
        return services;
    }

    /// <summary>Map کردن endpointها: <c>app.MapAiAssistant();</c> (بعد از UseAuthentication/UseAuthorization)</summary>
    public static IEndpointRouteBuilder MapAiAssistant(this IEndpointRouteBuilder app)
    {
        var options = app.ServiceProvider.GetRequiredService<AiAssistantOptions>();
        var busy = new ConcurrentDictionary<string, byte>();
        var root = app.MapGroup(options.RoutePrefix).ExcludeFromDescription();

        // JS مستقل: هر صفحه‌ای که این فایل را import کند دکمه و استایل را خودش می‌سازد.
        root.MapGet("/assistant.js", () => Results.Text(ReadAsset("assistant.js"), "application/javascript; charset=utf-8"))
            .AllowAnonymous();

        // ---- چت (نیازمند ورود کاربر؛ بدون Redirect تا fetch بتواند 401 را بخواند) ----
        var api = root.MapGroup("/api").AddEndpointFilter(async (ctx, next) =>
        {
            var http = ctx.HttpContext;
            ApplyCors(http, options);

            // 1) CSRF: درخواست‌های غیر GET باید هدر سفارشی داشته باشند (در cross-origin فقط از Origin مجاز عبور می‌کند).
            if (!HttpMethods.IsGet(http.Request.Method) && http.Request.Headers["X-Requested-With"] != "XMLHttpRequest")
                return Results.BadRequest(new { success = false, error = "درخواست نامعتبر است." });

            // 2) توکن سایت میزبان → کاربر. توکن نامعتبر = ناشناس (حتی اگر کوکی هم باشد).
            if (options.TokenResolver is not null
                && http.Request.Headers.TryGetValue(TokenHeader, out var token) && !string.IsNullOrWhiteSpace(token))
            {
                http.User = await options.TokenResolver(http, token.ToString().Trim()) ?? new ClaimsPrincipal(new ClaimsIdentity());
            }
            return await next(ctx);
        });

        // پاسخ Preflight برای CORS
        root.MapMethods("/api/{**path}", ["OPTIONS"], (HttpContext http) =>
        {
            ApplyCors(http, options);
            return Results.NoContent();
        }).AllowAnonymous();

        api.MapGet("/config", async (HttpContext ctx, AiAssistantStore store) =>
        {
            if (!await options.ChatAccess(ctx)) return Results.Ok(new { enabled = false });
            var s = await store.GetSettingsAsync(ctx.RequestAborted);
            return Results.Ok(new
            {
                enabled = true,
                welcomeMessage = s.Get("WelcomeMessage"),
                panelTitle = s.Get("PanelTitle"),
                buttonTitle = s.Get("ButtonTitle"),
                inputPlaceholder = s.Get("InputPlaceholder"),
                maxLength = s.MaxQuestionLength
            });
        }).AllowAnonymous();

        api.MapPost("/chat", async (ChatRequest request, HttpContext ctx, AiChatService chat, AiAssistantStore store) =>
        {
            if (!await options.ChatAccess(ctx)) return Results.Json(new { success = false }, statusCode: 401);

            var question = request.Message?.Trim();
            if (string.IsNullOrEmpty(question))
                return Results.BadRequest(new { success = false, error = "پیام نمی‌تواند خالی باشد." });

            var key = ctx.User.Identity?.Name ?? ctx.User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                ?? ctx.Connection.RemoteIpAddress?.ToString() ?? "anonymous";
            if (!busy.TryAdd(key, 0))
            {
                var s = await store.GetSettingsAsync(ctx.RequestAborted);
                return Results.Json(new { success = false, error = s.Get("BusyMessage") }, statusCode: 429);
            }

            try
            {
                var answer = await chat.SendMessageAsync(options.EvaluateParameters(ctx), question, request.ConversationId, ctx.RequestAborted);
                return Results.Ok(new { success = true, answer });
            }
            catch (AiAssistantException ex)
            {
                return Results.Ok(new { success = false, error = ex.Message });
            }
            finally
            {
                busy.TryRemove(key, out _);
            }
        }).AllowAnonymous();

        // ---- مدیریت: فقط وقتی GuiPolicy تعریف شده باشد ----
        if (options.GuiAccess is null)
        {
            app.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("AiAssistant")
                .LogWarning("GuiPolicy تعریف نشده است؛ صفحه مدیریت دستیار هوشمند غیرفعال است.");
            return app;
        }

        static IResult Denied(HttpContext c) => Results.StatusCode(c.User.Identity?.IsAuthenticated == true ? 403 : 401);

        root.MapGet("/admin", async (HttpContext ctx) =>
        {
            if (!await options.GuiAccess(ctx)) return Denied(ctx);
            ctx.Response.Headers.CacheControl = "no-store";
            return Results.Text(BuildAdminPage(options), "text/html; charset=utf-8");
        }).AllowAnonymous();

        var admin = api.MapGroup("/admin").AddEndpointFilter(async (ctx, next) =>
            await options.GuiAccess(ctx.HttpContext) ? await next(ctx) : Denied(ctx.HttpContext));

        admin.MapGet("/questions", async (AiAssistantStore s, CancellationToken ct) => await s.ListQuestionsAsync(false, ct));
        admin.MapPost("/questions", async (AiQuestion q, AiAssistantStore s, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(q.Question) || string.IsNullOrWhiteSpace(q.Answer))
                return Results.BadRequest(new { error = "سؤال و پاسخ الزامی است." });
            q.Category = string.IsNullOrWhiteSpace(q.Category) ? "عمومی" : q.Category.Trim();
            return Results.Ok(new { id = await s.SaveQuestionAsync(q, ct) });
        });
        admin.MapDelete("/questions/{id:int}", async (int id, AiAssistantStore s, CancellationToken ct) =>
        {
            await s.DeleteQuestionAsync(id, ct);
            return Results.NoContent();
        });

        admin.MapGet("/sections", async (AiAssistantStore s, CancellationToken ct) =>
        {
            var list = await s.ListSectionsAsync(false, ct);
            foreach (var item in list) item.ConnectionString = MaskPassword(item.ConnectionString);
            return list;
        });
        admin.MapPost("/sections", async (AiSection sec, AiAssistantStore s, AiContextBuilder builder, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(sec.Title) || string.IsNullOrWhiteSpace(sec.Query))
                return Results.BadRequest(new { error = "عنوان و Query الزامی است." });
            if (!builder.IsQueryAllowed(sec))
                return Results.BadRequest(new
                {
                    error = $"Query مجاز نیست: فقط SELECT تک‌دستوری، بدون دستور تغییر و بدون ; و کامنت. " +
                            (options.RequiredParameter is null ? "" : $"Query غیرعمومی باید @{options.RequiredParameter} داشته باشد.")
                });
            // رمز در لیست مخفی نمایش داده می‌شود؛ اگر کاربر آن را تغییر نداده، مقدار ذخیره‌شده حفظ می‌شود.
            if (sec.Id != 0 && sec.ConnectionString?.Contains(PasswordMask) == true)
                sec.ConnectionString = (await s.ListSectionsAsync(false, ct)).FirstOrDefault(x => x.Id == sec.Id)?.ConnectionString;
            return Results.Ok(new { id = await s.SaveSectionAsync(sec, ct) });
        });
        admin.MapPost("/sections/test", async (AiSection sec, HttpContext ctx, AiAssistantStore s, AiContextBuilder builder) =>
        {
            if (sec.Id != 0 && sec.ConnectionString?.Contains(PasswordMask) == true)
                sec.ConnectionString = (await s.ListSectionsAsync(false, ctx.RequestAborted)).FirstOrDefault(x => x.Id == sec.Id)?.ConnectionString;
            var settings = await s.GetSettingsAsync(ctx.RequestAborted);
            return Results.Ok(await builder.TestSectionAsync(sec, options.EvaluateParameters(ctx), settings, ctx.RequestAborted));
        });

        admin.MapGet("/meta", () => Results.Ok(new
        {
            parameters = options.Parameters.Keys.OrderBy(k => k).ToArray(),
            requiredParameter = options.RequiredParameter,
            namedConnections = options.QueryConnections.Keys.OrderBy(k => k).ToArray(),
            routePrefix = options.RoutePrefix
        }));

        admin.MapDelete("/sections/{id:int}", async (int id, AiAssistantStore s, CancellationToken ct) =>
        {
            await s.DeleteSectionAsync(id, ct);
            return Results.NoContent();
        });

        admin.MapGet("/settings", async (AiAssistantStore s, CancellationToken ct) =>
        {
            var current = await s.GetSettingsAsync(ct);
            return AiSettings.Definitions.Select(d => new
            {
                d.Key, d.Group, d.Description, d.Multiline, value = current.Get(d.Key), @default = d.Default
            });
        });
        admin.MapPut("/settings", async (Dictionary<string, string> values, AiAssistantStore s, CancellationToken ct) =>
        {
            await s.SaveSettingsAsync(values, ct);
            return Results.NoContent();
        });

        // پیش‌نمایش Context کامل برای کاربر جاری (همان چیزی که به مدل می‌رسد).
        admin.MapPost("/preview", async (HttpContext ctx, AiAssistantStore s, AiContextBuilder builder) =>
        {
            var settings = await s.GetSettingsAsync(ctx.RequestAborted);
            var context = await builder.BuildContextAsync(options.EvaluateParameters(ctx), settings, ctx.RequestAborted);
            return Results.Ok(new { length = context.Length, context });
        });

        // تست اتصال به Ollama و وجود مدل با یک پیام کوتاه.
        admin.MapPost("/model/test", async (AiChatService chat, CancellationToken ct) =>
        {
            var r = await chat.TestModelAsync(ct);
            return Results.Ok(new { ok = r.Ok, message = r.Message, elapsedMs = r.ElapsedMs, model = r.Model });
        });

        admin.MapGet("/logs", async (AiAssistantStore s, CancellationToken ct) => await s.ListLogsAsync(200, ct));

        return app;
    }

    private const string PasswordMask = "********";

    private static string? MaskPassword(string? value)
        => value is null ? null
            : System.Text.RegularExpressions.Regex.Replace(value, @"(?i)(password|pwd)\s*=\s*[^;]*",
                m => m.Value.Split('=')[0] + "=" + PasswordMask);

    private sealed record ChatRequest(string? Message, string? ConversationId = null);

    private const string TokenHeader = "X-Ai-Token";

    /// <summary>فقط برای Originهای ثبت‌شده در AllowedOrigins هدر CORS می‌گذارد.</summary>
    private static void ApplyCors(HttpContext http, AiAssistantOptions options)
    {
        var origin = http.Request.Headers.Origin.ToString().TrimEnd('/');
        if (origin.Length == 0 || !options.AllowedOriginList.Contains(origin)) return;

        var h = http.Response.Headers;
        h["Access-Control-Allow-Origin"] = origin;
        h["Vary"] = "Origin";
        h["Access-Control-Allow-Headers"] = "Content-Type, X-Requested-With, " + TokenHeader;
        h["Access-Control-Allow-Methods"] = "GET, POST, PUT, DELETE, OPTIONS";
        h["Access-Control-Max-Age"] = "600";

        // Private Network Access: مرورگر برای درخواست از سایت عمومی به localhost/شبکه داخلی این هدر را در Preflight می‌خواهد.
        if (http.Request.Headers.ContainsKey("Access-Control-Request-Private-Network"))
            h["Access-Control-Allow-Private-Network"] = "true";
    }

    /// <summary>صفحه مدیریت + لینک CSS/JS تزریق‌شده توسط میزبان (Program.cs).</summary>
    private static string BuildAdminPage(AiAssistantOptions options)
    {
        var css = string.Concat(options.AdminStylesheets.Select(u =>
            $"<link rel=\"stylesheet\" href=\"{System.Net.WebUtility.HtmlEncode(u)}\">\n"));
        var js = string.Concat(options.AdminScripts.Select(u =>
            $"<script src=\"{System.Net.WebUtility.HtmlEncode(u)}\" defer></script>\n"));
        return ReadAsset("admin.html")
            .Replace("<!--AI-ADMIN-HEAD-->", css)
            .Replace("<!--AI-ADMIN-BODY-->", js);
    }

    private static readonly ConcurrentDictionary<string, string> AssetCache = new();

    private static string ReadAsset(string name)
        => AssetCache.GetOrAdd(name, n =>
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("AiAssistant.Assets." + n)
                ?? throw new InvalidOperationException($"Embedded asset not found: {n}");
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        });
}

/// <summary>ساخت جدول‌ها و Seed هنگام راه‌اندازی؛ خطا برنامه را متوقف نمی‌کند و در اولین استفاده دوباره تلاش می‌شود.</summary>
internal sealed class AiAssistantInitializer(AiAssistantStore store, ILogger<AiAssistantInitializer> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try { await store.EnsureReadyAsync(cancellationToken); }
        catch (Exception ex) { logger.LogError(ex, "AI assistant database initialization failed"); }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
