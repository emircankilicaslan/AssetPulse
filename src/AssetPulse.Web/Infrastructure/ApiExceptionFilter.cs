using AssetPulse.Web.Api;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace AssetPulse.Web.Infrastructure;

/// <summary>
/// API çağrılarından gelen ortak hataları kullanıcı dostu sonuçlara çevirir:
/// 401 → çıkış yapıp girişe yönlendir, 403 → yetki sayfası, 404 → bulunamadı, ulaşılamıyor → 503 sayfası.
/// Form işlemleri hatayı kendisi yakalayıp formda gösterir; buraya yalnızca yakalanmayanlar düşer.
/// </summary>
public sealed class ApiExceptionFilter(ILogger<ApiExceptionFilter> logger) : IAsyncExceptionFilter
{
    public async Task OnExceptionAsync(ExceptionContext context)
    {
        var http = context.HttpContext;
        var isFetch = http.Request.Headers["X-Requested-With"] == "fetch"; // grafik istekleri JSON bekler

        switch (context.Exception)
        {
            case ApiUnauthorizedException:
                await http.SignOutAsync();
                context.Result = isFetch
                    ? new StatusCodeResult(StatusCodes.Status401Unauthorized)
                    : new RedirectToActionResult("Login", "Account", new { returnUrl = http.Request.Path + http.Request.QueryString });
                break;
            case ApiForbiddenException:
                context.Result = isFetch
                    ? new StatusCodeResult(StatusCodes.Status403Forbidden)
                    : new RedirectToActionResult("AccessDenied", "Account", null);
                break;
            case ApiException { Status: System.Net.HttpStatusCode.NotFound }:
                context.Result = new StatusCodeResult(StatusCodes.Status404NotFound);
                break;
            case ApiException ex:
                logger.LogWarning("API hatası {Status}: {Message}", (int)ex.Status, ex.Message);
                context.Result = new StatusCodeResult(StatusCodes.Status502BadGateway);
                break;
            case HttpRequestException or TaskCanceledException { InnerException: TimeoutException }:
                logger.LogWarning(context.Exception, "API'ye ulaşılamadı.");
                context.Result = new StatusCodeResult(StatusCodes.Status503ServiceUnavailable);
                break;
            default:
                return;
        }
        context.ExceptionHandled = true;
    }
}
