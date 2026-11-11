using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;

namespace Sam.AiAssistant.Web.Controllers;

/// <summary>
/// ورود آزمایشی (بدون رمز) فقط برای نمایش ماژول؛ در پروژه واقعی از احراز هویت خودتان استفاده کنید.
/// </summary>
public class AccountController : Controller
{
    [HttpGet]
    public IActionResult Login() => View();

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> LoginAs(string role)
    {
        var isAdmin = role == "admin";
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, isAdmin ? "2" : "1"),
            new(ClaimTypes.Name, isAdmin ? "admin" : "user")
        };
        if (isAdmin) claims.Add(new Claim(ClaimTypes.Role, "admin"));

        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme)));
        return RedirectToAction("Index", "Home");
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return RedirectToAction("Index", "Home");
    }
}
