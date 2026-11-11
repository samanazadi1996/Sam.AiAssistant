# Sam.AiAssistant

دو پروژه:

| پروژه | توضیح |
| --- | --- |
| `src/Sam.AiAssistant` | کتابخانه‌ی ماژول دستیار هوشمند (Ollama): جدول‌های خودش، پنل مدیریت، API چت، ویجت JS مستقل |
| `src/Sam.AiAssistant.Web` | برنامه‌ی MVC ساده که ماژول را ثبت و استفاده می‌کند (ورود آزمایشی، seed نمونه) |

## اجرا
1. SQL Server در دسترس باشد؛ رشته‌ی اتصال `AiAssistantData` در `src/Sam.AiAssistant.Web/appsettings.json`
   (دیتابیس `SamAiAssistantDB` و جدول‌ها در schema `AiAssistant` خودکار ساخته می‌شوند).
2. Ollama: `ollama pull qwen2.5:7b` (آدرس/مدل از پنل مدیریت قابل تغییر است).
3. `dotnet run --project src/Sam.AiAssistant.Web`
4. در صفحه‌ی ورود «مدیر» را بزنید؛ دکمه‌ی ربات پایین-چپ و پنل `/ai-assistant/admin` در دسترس است.

## Seed
۱۱ سؤال عمومی و ۳ Section نمونه (همگی **غیرفعال**؛ Queryها نمونه‌اند و باید با دیتابیس واقعی تطبیق داده شوند).
Seed فقط یک‌بار درج می‌شود؛ بعد از آن از پنل مدیریت ویرایش کنید.

مستندات ماژول: `src/Sam.AiAssistant/README.md`
