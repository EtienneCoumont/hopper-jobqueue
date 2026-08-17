using System.Security.Claims;
using HopperJobQueue.Api.Auth;
using HopperJobQueue.Api.Domain;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace HopperJobQueue.Api.Pages.Admin;

public sealed class LoginModel(ApiKeyStore apiKeyStore) : PageModel
{
    public string? Error { get; private set; }

    public IActionResult OnGet()
    {
        if (User.Identity?.IsAuthenticated == true)
        {
            return Redirect("/admin");
        }

        return Page();
    }

    public async Task<IActionResult> OnPostAsync([FromForm] string? key, [FromQuery] string? returnUrl)
    {
        var record = string.IsNullOrWhiteSpace(key)
            ? null
            : await apiKeyStore.AuthenticateAsync(key.Trim(), HttpContext.RequestAborted);

        if (record is null || record.Scope != ApiScope.Admin)
        {
            // Message générique : ne pas distinguer clé inconnue / révoquée / mauvais scope.
            Error = "Clé invalide.";
            return Page();
        }

        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, record.Id.ToString()),
                new Claim(ClaimTypes.Name, record.Name),
            ],
            CookieAuthenticationDefaults.AuthenticationScheme);
        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity));

        var target = returnUrl is not null && Url.IsLocalUrl(returnUrl) && returnUrl.StartsWith("/admin", StringComparison.Ordinal)
            ? returnUrl
            : "/admin";
        return Redirect(target);
    }

    public async Task<IActionResult> OnPostLogoutAsync()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return Redirect("/admin/login");
    }
}
