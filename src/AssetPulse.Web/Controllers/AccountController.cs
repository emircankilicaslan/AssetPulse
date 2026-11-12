using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using AssetPulse.Web.Api;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace AssetPulse.Web.Controllers;

public sealed class LoginViewModel
{
    [Required(ErrorMessage = "Kullanıcı adı zorunludur.")]
    [Display(Name = "Kullanıcı adı")]
    public string Username { get; set; } = "";

    [Required(ErrorMessage = "Parola zorunludur.")]
    [DataType(DataType.Password)]
    [Display(Name = "Parola")]
    public string Password { get; set; } = "";

    public string? ReturnUrl { get; set; }
}

[AllowAnonymous]
public sealed class AccountController(ApiClient api) : Controller
{
    [HttpGet]
    public IActionResult Login(string? returnUrl) =>
        User.Identity?.IsAuthenticated == true
            ? RedirectToAction("Index", "Home")
            : View(new LoginViewModel { ReturnUrl = returnUrl });

    [HttpPost]
    [EnableRateLimiting("login")]
    public async Task<IActionResult> Login(LoginViewModel model, CancellationToken ct)
    {
        if (!ModelState.IsValid) return View(model);

        LoginResult? result;
        try
        {
            result = await api.LoginAsync(model.Username.Trim(), model.Password, ct);
        }
        catch (ApiException ex)
        {
            return Fail(model, ex.Message);
        }
        catch (HttpRequestException)
        {
            return Fail(model, "Servise şu anda ulaşılamıyor. Lütfen biraz sonra tekrar deneyin.");
        }

        if (result is null) return Fail(model, "Kullanıcı adı veya parola hatalı.");

        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, result.Username), new Claim(ClaimTypes.Role, result.Role)],
            CookieAuthenticationDefaults.AuthenticationScheme);
        var props = new AuthenticationProperties
        {
            ExpiresUtc = new DateTimeOffset(DateTime.SpecifyKind(result.ExpiresAt, DateTimeKind.Utc)),
            AllowRefresh = false,
        };
        // JWT, şifrelenmiş kimlik çerezinin içinde saklanır; tarayıcı JavaScript'i erişemez.
        props.StoreTokens([new AuthenticationToken { Name = "access_token", Value = result.Token }]);
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity), props);

        return Url.IsLocalUrl(model.ReturnUrl) ? LocalRedirect(model.ReturnUrl) : RedirectToAction("Index", "Home");
    }

    [HttpPost]
    [Authorize]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return RedirectToAction(nameof(Login));
    }

    [HttpGet]
    public IActionResult AccessDenied() => View();

    private ViewResult Fail(LoginViewModel model, string message)
    {
        ModelState.AddModelError("", message);
        model.Password = "";
        return View(model);
    }
}
