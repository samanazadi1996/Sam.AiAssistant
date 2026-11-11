# Sam.AiAssistant

دستیار هوشمند مستقل (Ollama) برای هر پروژه ASP.NET Core. جدول‌ها، صفحه مدیریت، API و ویجت چت را خودش دارد.

## استفاده

```csharp
// Program.cs
builder.Services.AddAiAssistant(o =>
{
    o.DataConnectionString  = "...";   // دیتابیس خود دستیار (جدول‌ها اینجا ساخته می‌شوند: schema AiAssistant)
    o.QueryConnectionString = "...";   // دیتابیسی که Queryها روی آن اجرا می‌شوند (ترجیحاً کاربر فقط‌خواندنی)
    o.AddQueryConnection("Reports", "...");   // اختیاری: اتصال نام‌دار؛ هر Section می‌تواند نام آن یا یک Connection String کامل بنویسد
    o.AddAdminStylesheet("/css/my-admin.css");   // اختیاری: CSS/JS تزریقی به صفحه مدیریت
    o.AddAdminScript("/js/my-admin.js");
    // دسترسی به پنل مدیریت (/ai-assistant/admin). تعریف نشود = پنل Map نمی‌شود.
    o.GuiPolicy(ctx => ctx.User.Identity?.IsAuthenticated == true && ctx.User.IsInRole("admin"));
    // دسترسی به چت و دکمه (اختیاری؛ پیش‌فرض: هر کاربر وارد‌شده)
    // o.ChatPolicy(ctx => ctx.User.Identity?.IsAuthenticated == true);

    // پارامتر دلخواه برای همه Queryها (@Name). هر تعداد می‌توانید اضافه کنید.
    o.AddParameter("UserId", ctx => int.Parse(ctx.User.FindFirst(ClaimTypes.NameIdentifier)!.Value));
    o.AddParameter("CurrentDateTime", _ => DateTime.Now);
    o.AddParameter("TenantId", ctx => ctx.User.FindFirst("tenant")?.Value);

    o.SeedQuestions = ...; o.SeedSections = ...;   // اختیاری، فقط یک‌بار Seed می‌شود
});

app.UseAuthentication(); app.UseAuthorization();
app.MapAiAssistant();
```

در هر صفحه (مثلاً Layout) فقط یک خط:

```html
<script src="/ai-assistant/assistant.js" defer></script>
```
دکمه ربات متحرک (پیش‌فرض پایین-چپ؛ `data-position="right"`، `data-dir="ltr"` اختیاری) و تمام استایل‌ها داخل Shadow DOM ساخته می‌شود. فقط برای کاربر وارد‌شده نمایش داده می‌شود.

## مسیرها
| مسیر | کار |
| --- | --- |
| `GET /ai-assistant/assistant.js` | ویجت |
| `GET /ai-assistant/admin` | صفحه مدیریت (سوال/جواب، Section، تنظیمات، پیش‌نمایش Context، گفتگوها) |
| `POST /ai-assistant/api/chat` | `{ "message": "..." }` → `{ success, answer }` |

## جدول‌ها (ساخت خودکار)
`AiAssistant.Settings`، `Questions`، `Sections`، `ChatLogs`. کاربر `DataConnectionString` باید اجازه‌ی ساخت schema/table داشته باشد (یا `AutoCreateTables=false` و DDL را دستی اجرا کنید).

## قابل تغییر از صفحه مدیریت
System Message، قالب پیام کاربر، قالب کل Context (`{{KnowledgeBase}}`، `{{Sections}}`، `{{Param.نام}}`)، قالب هر سوال/Section، همه پیام‌های خطا و رابط کاربری، مدل/آدرس/Temperature، محدودیت‌ها.

## امنیت
- UserId و بقیه پارامترها فقط از `AddParameter` (سمت سرور) می‌آیند؛ نه از کاربر و نه از مدل.
- Query فقط `SELECT`/`WITH` تک‌دستوری؛ دستورهای تغییر، `;`، کامنت ممنوع. Query غیرعمومی باید `@UserId` (یا `RequiredParameter`) داشته باشد.
- ستون‌های password/token/otp/secret/hash/fcm هرگز وارد Context نمی‌شوند؛ خطای SQL به مدل/کاربر نمی‌رسد.
- درخواست‌های غیر GET نیازمند هدر `X-Requested-With: XMLHttpRequest` هستند (دفاع CSRF).
- ویرایش Query در صفحه مدیریت معادل اجرای SQL است: `GuiPolicy` را سخت‌گیرانه بنویسید و `QueryConnectionString` را فقط‌خواندنی.

## استفاده در سایت دیگر (Cross-site)
ویجت را می‌توان در هر سایتی گذاشت؛ هویت کاربر با توکن می‌آید و سرور آن را «باز می‌کند».

```html
<script src="https://support.example.com/ai-assistant/assistant.js" data-token-fn="getAiToken" defer></script>
<script> window.getAiToken = async () => myToken; /* JWT / کلید / شناسه ملی / ... */ </script>
```
(یا مقدار ثابت با `data-token="..."`). توکن در هدر `X-Ai-Token` ارسال می‌شود.

```csharp
o.AllowedOrigins("https://shop.example.com");               // CORS؛ خالی = فقط همین دامنه

o.ResolveToken(async (ctx, token) =>                         // توکن → کاربر (null = نامعتبر)
{
    var userId = await MyTokenService.ValidateAsync(token);  // JWT، کلید API، شناسه ملی، ...
    return userId is null ? null : new ClaimsPrincipal(new ClaimsIdentity(
        new[] { new Claim(ClaimTypes.NameIdentifier, userId) }, "AiToken"));
});

o.ChatPolicy(ctx => ctx.User.Identity?.IsAuthenticated ?? false);   // کاربر چت
o.GuiPolicy(ctx => ctx.User.IsInRole("admin"));                      // مدیر پنل (جدا)
```
کاربر برگردانده‌شده از `ResolveToken` همان `ctx.User` است که در `ChatPolicy`، `GuiPolicy` و `AddParameter` (مثلاً UserId) استفاده می‌شود. وقتی ویجت روی دامنه‌ای غیر از سرور است کوکی ارسال نمی‌شود و فقط توکن معتبر است. در سایت خودتان نیازی به توکن نیست و کوکی کافی است.
