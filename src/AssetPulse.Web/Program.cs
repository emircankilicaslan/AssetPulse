using System.Text.Encodings.Web;
using System.Text.Unicode;
using System.Threading.RateLimiting;
using AssetPulse.Web.Api;
using AssetPulse.Web.Helpers;
using AssetPulse.Web.Infrastructure;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Authorization;
using Microsoft.Extensions.WebEncoders;

var builder = WebApplication.CreateBuilder(args);

var apiBase = builder.Configuration["Api:BaseUrl"];
if (string.IsNullOrWhiteSpace(apiBase))
    throw new InvalidOperationException("Api:BaseUrl tanımlı olmalı (ör. http://localhost:8080).");
Display.UtcOffset = TimeSpan.FromHours(builder.Configuration.GetValue("Display:UtcOffsetHours", 3.0));

// Türkçe karakterler HTML çıktısında &#xFC; gibi kodlanmasın (varsayılan yalnızca Basic Latin'e izin verir).
builder.Services.Configure<WebEncoderOptions>(o => o.TextEncoderSettings = new TextEncoderSettings(UnicodeRanges.All));
builder.Services.AddHttpContextAccessor();
builder.Services.AddHttpClient<ApiClient>(client =>
{
    client.BaseAddress = new Uri(apiBase.TrimEnd('/') + "/");
    client.Timeout = TimeSpan.FromSeconds(15);
});

builder.Services.AddControllersWithViews(o =>
{
    o.Filters.Add(new AuthorizeFilter());                       // varsayılan: giriş zorunlu
    o.Filters.Add(new AutoValidateAntiforgeryTokenAttribute()); // tüm POST'larda CSRF doğrulaması
    o.Filters.Add<ApiExceptionFilter>();
});

builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.Cookie.Name = "assetpulse.auth";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Lax;
        // Demo HTTP üzerinde çalışır; HTTPS arkasında "Always" kullanılmalıdır (README).
        o.Cookie.SecurePolicy = builder.Configuration.GetValue("Auth:SecureCookie", false)
            ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
        o.LoginPath = "/Account/Login";
        o.AccessDeniedPath = "/Account/AccessDenied";
        o.SlidingExpiration = false; // çerez ömrü API token ömrüyle aynıdır
    });

builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("login", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "bilinmiyor",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1) }));
});

var app = builder.Build();

app.UseExceptionHandler("/Home/Status?code=500");
app.UseStatusCodePagesWithReExecute("/Home/Status", "?code={0}");

app.Use(async (ctx, next) =>
{
    var h = ctx.Response.Headers;
    h["X-Content-Type-Options"] = "nosniff";
    h["X-Frame-Options"] = "DENY";
    h["Referrer-Policy"] = "same-origin";
    h["Content-Security-Policy"] =
        "default-src 'self'; img-src 'self' data:; style-src 'self'; script-src 'self'; " +
        "frame-ancestors 'none'; base-uri 'self'; form-action 'self'";
    await next();
});

app.UseStaticFiles();
app.UseRequestLocalization(new RequestLocalizationOptions()
    .SetDefaultCulture("tr-TR").AddSupportedCultures("tr-TR").AddSupportedUICultures("tr-TR"));
app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new { status = "ok" })).AllowAnonymous();
app.MapControllerRoute("default", "{controller=Home}/{action=Index}/{id?}");

app.Run();

public partial class Program { }
