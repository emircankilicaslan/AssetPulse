using AssetPulse.Web.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AssetPulse.Web.Controllers;

public sealed class HomeController(ApiClient api) : Controller
{
    public async Task<IActionResult> Index(CancellationToken ct) => View(await api.DashboardAsync(ct));

    /// <summary>Hata/durum sayfası (404, 502, 503 …). Durum kodları UseStatusCodePagesWithReExecute ile buraya gelir.</summary>
    [AllowAnonymous]
    public IActionResult Status(int code)
    {
        Response.StatusCode = code;
        return View(code);
    }
}
