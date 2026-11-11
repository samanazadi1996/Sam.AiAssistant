using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;

namespace Sam.AiAssistant;

/// <summary>تنظیمات ثبت کتابخانه در Program.cs.</summary>
public sealed class AiAssistantOptions
{
    /// <summary>دیتابیسی که جدول‌های خود دستیار (سوال/جواب، Section، تنظیمات، لاگ) در آن ساخته می‌شود.</summary>
    public string DataConnectionString { get; set; } = "";

    /// <summary>دیتابیسی که Queryهای Sectionها روی آن اجرا می‌شوند. توصیه: کاربر فقط‌خواندنی (db_datareader).</summary>
    public string QueryConnectionString { get; set; } = "";

    /// <summary>پیشوند همه مسیرها: {prefix}/assistant.js ، {prefix}/admin ، {prefix}/api/...</summary>
    public string RoutePrefix { get; set; } = "/ai-assistant";

    /// <summary>
    /// دامنه‌هایی که اجازه دارند ویجت را در سایت خودشان بگذارند و از آنجا به API این سرویس درخواست بزنند (CORS).
    /// مثال: <c>o.AllowedOrigins("https://shop.example.com")</c>. خالی = فقط همین دامنه.
    /// </summary>
    public AiAssistantOptions AllowedOrigins(params string[] origins)
    {
        foreach (var o in origins) AllowedOriginList.Add(o.Trim().TrimEnd('/'));
        return this;
    }

    internal HashSet<string> AllowedOriginList { get; } = new(StringComparer.OrdinalIgnoreCase);

    internal Func<HttpContext, string, Task<ClaimsPrincipal?>>? TokenResolver { get; private set; }

    /// <summary>
    /// تبدیل توکنی که ویجت می‌فرستد (هدر X-Ai-Token؛ هر چیزی: JWT، کلید، شناسه ملی، ...) به کاربر.
    /// خروجی به‌عنوان <c>ctx.User</c> در GuiPolicy، ChatPolicy و AddParameterها در دسترس است.
    /// null برگردانید = توکن نامعتبر (کاربر ناشناس).
    /// </summary>
    public AiAssistantOptions ResolveToken(Func<HttpContext, string, Task<ClaimsPrincipal?>> resolver)
    {
        TokenResolver = resolver;
        return this;
    }

    public AiAssistantOptions ResolveToken(Func<HttpContext, string, ClaimsPrincipal?> resolver)
    {
        TokenResolver = (ctx, token) => Task.FromResult(resolver(ctx, token));
        return this;
    }

    internal Func<HttpContext, Task<bool>>? GuiAccess { get; private set; }

    internal Func<HttpContext, Task<bool>> ChatAccess { get; private set; }
        = ctx => Task.FromResult(ctx.User.Identity?.IsAuthenticated == true);

    /// <summary>
    /// چه کسی به پنل مدیریت (/admin و API آن) دسترسی دارد. تعریف نکنید = پنل اصلاً Map نمی‌شود.
    /// مثال: <c>o.GuiPolicy(ctx =&gt; ctx.User.IsInRole("admin"))</c>. برای باز گذاشتن برای همه: <c>ctx =&gt; true</c> (فقط توسعه).
    /// </summary>
    public AiAssistantOptions GuiPolicy(Func<HttpContext, bool> allow)
    {
        GuiAccess = ctx => Task.FromResult(allow(ctx));
        return this;
    }

    public AiAssistantOptions GuiPolicy(Func<HttpContext, Task<bool>> allow)
    {
        GuiAccess = allow;
        return this;
    }

    /// <summary>
    /// چه کسی می‌تواند چت کند (و دکمه را ببیند). پیش‌فرض: هر کاربر احراز هویت‌شده.
    /// اگر ناشناس‌ها را هم مجاز کنید، پارامترهای وابسته به User (مثل UserId) برای آن‌ها null می‌شوند.
    /// </summary>
    public AiAssistantOptions ChatPolicy(Func<HttpContext, bool> allow)
    {
        ChatAccess = ctx => Task.FromResult(allow(ctx));
        return this;
    }

    public AiAssistantOptions ChatPolicy(Func<HttpContext, Task<bool>> allow)
    {
        ChatAccess = allow;
        return this;
    }

    /// <summary>
    /// نام پارامتری که هر Query غیرعمومی حتماً باید به آن اشاره کند (مثلاً UserId) تا داده کاربران دیگر نخوانده شود.
    /// null یعنی بررسی نشود.
    /// </summary>
    public string? RequiredParameter { get; set; } = "UserId";

    /// <summary>
    /// اگر دیتابیسِ DataConnectionString وجود نداشت آن را بسازد (نیازمند دسترسی CREATE DATABASE روی سرور). پیش‌فرض false.
    /// </summary>
    public bool AutoCreateDatabase { get; set; }

    /// <summary>ساخت خودکار جدول‌ها و Seed در اولین اجرا.</summary>
    public bool AutoCreateTables { get; set; } = true;

    /// <summary>ذخیره‌ی پرسش‌ها و پاسخ‌ها در جدول ChatLogs.</summary>
    public bool LogConversations { get; set; } = true;

    /// <summary>داده‌ی اولیه؛ فقط یک‌بار و فقط وقتی جدول مربوط هنوز Seed نشده است درج می‌شود.</summary>
    public List<AiQuestion> SeedQuestions { get; set; } = new();
    public List<AiSection> SeedSections { get; set; } = new();

    /// <summary>مقدار اولیه تنظیمات (فقط برای کلیدهایی که هنوز در دیتابیس نیستند).</summary>
    public Dictionary<string, string> SettingDefaults { get; set; } = new();

    /// <summary>فایل‌های CSS که در انتهای &lt;head&gt; صفحه مدیریت تزریق می‌شوند (می‌توانند ظاهر و فونت را بازنویسی کنند).</summary>
    public List<string> AdminStylesheets { get; } = new();

    /// <summary>فایل‌های JS که در انتهای &lt;body&gt; صفحه مدیریت تزریق می‌شوند.</summary>
    public List<string> AdminScripts { get; } = new();

    /// <summary>افزودن لینک CSS به صفحه مدیریت (مثلاً "/css/ai-admin-font.css").</summary>
    public AiAssistantOptions AddAdminStylesheet(string url) { AdminStylesheets.Add(CheckAssetUrl(url)); return this; }

    /// <summary>افزودن لینک JS به صفحه مدیریت.</summary>
    public AiAssistantOptions AddAdminScript(string url) { AdminScripts.Add(CheckAssetUrl(url)); return this; }

    private static string CheckAssetUrl(string url)
    {
        url = url?.Trim() ?? "";
        var ok = url.StartsWith("/") && !url.StartsWith("//") || url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        return ok ? url : throw new ArgumentException("آدرس باید با / یا http(s):// شروع شود.", nameof(url));
    }

    /// <summary>اتصال‌های نام‌دار؛ در Section می‌توان به‌جای Connection String کامل، فقط نام را نوشت.</summary>
    public Dictionary<string, string> QueryConnections { get; } = new(StringComparer.OrdinalIgnoreCase);

    public AiAssistantOptions AddQueryConnection(string name, string connectionString)
    {
        QueryConnections[name] = connectionString;
        return this;
    }

    /// <summary>اتصال مؤثر یک Section: نام ثبت‌شده، یا Connection String خودش، یا پیش‌فرض.</summary>
    internal string ResolveConnection(AiSection section)
    {
        var value = section.ConnectionString?.Trim();
        if (string.IsNullOrEmpty(value)) return QueryConnectionString;
        return QueryConnections.TryGetValue(value, out var named) ? named : value;
    }

    internal Dictionary<string, Func<HttpContext, object?>> Parameters { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// پارامتر دلخواه برای همه Queryها (به‌صورت @Name). مقدار در هر درخواست از روی HttpContext محاسبه می‌شود.
    /// </summary>
    public AiAssistantOptions AddParameter(string name, Func<HttpContext, object?> valueFactory)
    {
        if (!Regex.IsMatch(name, "^[A-Za-z_][A-Za-z0-9_]*$"))
            throw new ArgumentException("نام پارامتر نامعتبر است.", nameof(name));
        Parameters[name] = valueFactory;
        return this;
    }

    internal Dictionary<string, object?> EvaluateParameters(HttpContext context)
        {
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, factory) in Parameters)
        {
            try { result[name] = factory(context); }
            catch (Exception) when (context.User.Identity?.IsAuthenticated != true) { result[name] = null; }
        }
        return result;
    }

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(DataConnectionString))
            throw new InvalidOperationException("AiAssistant: DataConnectionString تنظیم نشده است.");
        if (string.IsNullOrWhiteSpace(QueryConnectionString))
            throw new InvalidOperationException("AiAssistant: QueryConnectionString (اتصال پیش‌فرض Queryها) تنظیم نشده است.");
        if (RequiredParameter is not null && !Parameters.ContainsKey(RequiredParameter))
            throw new InvalidOperationException(
                $"AiAssistant: پارامتر الزامی «{RequiredParameter}» با AddParameter تعریف نشده است.");
        RoutePrefix = "/" + RoutePrefix.Trim('/');
    }
}
