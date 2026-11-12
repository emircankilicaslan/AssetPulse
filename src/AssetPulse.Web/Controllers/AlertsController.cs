using AssetPulse.Web.Api;
using Microsoft.AspNetCore.Mvc;

namespace AssetPulse.Web.Controllers;

public sealed class AlertsIndexModel
{
    public required AlertPage Page { get; init; }
    public string? Status { get; init; }
    public string? Severity { get; init; }
}

public sealed class AlertsController(ApiClient api) : Controller
{
    private const int PageSize = 15;

    /// <summary>status: "open" (aktif + üstlenilmiş), Active, Acknowledged, Cleared veya boş (tümü).</summary>
    public async Task<IActionResult> Index(string? status, string? severity, int page = 1, CancellationToken ct = default)
    {
        var data = await api.AlertsAsync(ToFilter(status, severity), Math.Max(1, page), PageSize, ct);
        return View(new AlertsIndexModel { Page = data, Status = status, Severity = severity });
    }

    [HttpPost]
    public async Task<IActionResult> Acknowledge(long id, string? note, string? returnUrl, CancellationToken ct)
    {
        try
        {
            await api.AcknowledgeAsync(id, string.IsNullOrWhiteSpace(note) ? null : note.Trim(), ct);
            TempData["Success"] = "Alarm üstlenildi.";
        }
        catch (ApiException ex) when (ex.Status is System.Net.HttpStatusCode.Conflict or System.Net.HttpStatusCode.BadRequest or System.Net.HttpStatusCode.NotFound)
        {
            TempData["Error"] = ex.Message;
        }
        return Url.IsLocalUrl(returnUrl) ? LocalRedirect(returnUrl) : RedirectToAction(nameof(Index), new { status = "open" });
    }

    public async Task<IActionResult> Export(string? status, string? severity, CancellationToken ct)
    {
        var (content, fileName) = await api.ExportAlertsAsync(ToFilter(status, severity), ct);
        return File(content, "text/csv; charset=utf-8", fileName);
    }

    private static AlertFilter ToFilter(string? status, string? severity)
    {
        var open = status == "open";
        var exact = status is "Active" or "Acknowledged" or "Cleared" ? status : null;
        var sev = severity is "Info" or "Warning" or "Critical" ? severity : null;
        return new AlertFilter(exact, open, sev, null);
    }
}
