using AssetPulse.Web.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AssetPulse.Web.Controllers;

public sealed class AuditIndexModel
{
    public required AuditPage Page { get; init; }
    public string? Actor { get; init; }
    public string? Action { get; init; }
}

/// <summary>Denetim kaydı (kim, ne zaman, ne yaptı). Yalnızca Admin görür; API de aynı kısıtı uygular.</summary>
[Authorize(Roles = "Admin")]
public sealed class AuditController(ApiClient api) : Controller
{
    private const int PageSize = 20;

    public async Task<IActionResult> Index(string? actor, string? islem, int page = 1, CancellationToken ct = default)
    {
        var data = await api.AuditAsync(Clean(actor), Clean(islem), Math.Max(1, page), PageSize, ct);
        return View(new AuditIndexModel { Page = data, Actor = Clean(actor), Action = Clean(islem) });
    }

    public async Task<IActionResult> Export(string? actor, string? islem, CancellationToken ct)
    {
        var content = await api.ExportAuditXlsxAsync(Clean(actor), Clean(islem), ct);
        return File(content, XlsxType, "denetim-kaydi.xlsx");
    }

    internal const string XlsxType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
