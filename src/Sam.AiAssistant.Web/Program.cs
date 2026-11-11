using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.Cookies;
using Sam.AiAssistant;
using Sam.AiAssistant.Web.Seed;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllersWithViews();

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/Account/Login";
        options.AccessDeniedPath = "/Account/Login";
        options.Cookie.Name = "Sam.Auth";
    });
builder.Services.AddAuthorization();

// ---------------------------------------------------------------------------------------------
// دستیار هوشمند (ماژول Sam.AiAssistant)
// ---------------------------------------------------------------------------------------------
builder.Services.AddAiAssistant(o =>
{
    // دیتابیس خود دستیار (جدول‌ها در schema AiAssistant ساخته می‌شوند) و دیتابیسی که Queryها روی آن اجرا می‌شوند.
    var connection = builder.Configuration.GetConnectionString("AiAssistantData")!;
    o.DataConnectionString = connection;
    o.QueryConnectionString = builder.Configuration.GetConnectionString("AiAssistantQuery") ?? connection;
    o.AutoCreateDatabase = true;   // اگر دیتابیس وجود نداشت بساز (نیازمند دسترسی CREATE DATABASE)

    // مدیر پنل (/ai-assistant/admin)
    o.GuiPolicy(ctx => (ctx.User.Identity?.IsAuthenticated ?? false) && ctx.User.IsInRole("admin"));

    // کاربر چت و دکمه شناور
    o.ChatPolicy(ctx => ctx.User.Identity?.IsAuthenticated ?? false);

    // دامنه‌هایی که می‌توانند ویجت را در سایت خودشان بگذارند + تبدیل توکن آن‌ها به کاربر (اختیاری):
    // o.AllowedOrigins("https://other-site.example");
    // o.ResolveToken(async (ctx, token) => { ... return new ClaimsPrincipal(...); });

    // داده‌ی اولیه (فقط یک‌بار درج می‌شود؛ بعد از آن از صفحه مدیریت ویرایش کنید)
    o.SeedQuestions = AiSeed.Questions;
    o.SeedSections = AiSeed.Sections;

    // هر پارامتری که اینجا اضافه شود در همه Queryها به‌صورت @Name در دسترس است.
    o.AddParameter("UserId", ctx => int.Parse(ctx.User.FindFirst(ClaimTypes.NameIdentifier)!.Value));
    o.AddParameter("CurrentDateTime", _ => DateTime.Now);
    o.AddParameter("CurrentDate", _ => DateTime.Today);
});

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();

app.MapAiAssistant();

app.MapStaticAssets();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}")
    .WithStaticAssets();

app.Run();
