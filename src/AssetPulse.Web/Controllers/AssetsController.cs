using System.ComponentModel.DataAnnotations;
using AssetPulse.Web.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AssetPulse.Web.Controllers;

public sealed class AssetsIndexModel
{
    public required List<AssetSummary> Assets { get; init; }
    public required Dictionary<string, int> Counts { get; init; }
    public string? Health { get; init; }
}

public sealed class AssetDetailModel
{
    public required AssetDetail Asset { get; init; }
    public required AlertPage Alerts { get; init; }
    public string Health { get; init; } = "Normal";
}

public sealed class CreateAssetModel
{
    [Required(ErrorMessage = "Kod zorunludur.")]
    [RegularExpression("^[A-Za-z0-9][A-Za-z0-9._-]{1,39}$", ErrorMessage = "Kod 2-40 karakter olmalı; harf, rakam, '.', '_' ve '-' içerebilir.")]
    [Display(Name = "Varlık kodu")]
    public string Code { get; set; } = "";

    [Required(ErrorMessage = "Ad zorunludur."), StringLength(120)]
    [Display(Name = "Ad")]
    public string Name { get; set; } = "";

    [Required(ErrorMessage = "Kategori seçin.")]
    [Display(Name = "Kategori")]
    public string Category { get; set; } = "Transformer";

    [StringLength(120)]
    [Display(Name = "Konum")]
    public string? Location { get; set; }
}

public sealed class AssetsController(ApiClient api, TimeProvider clock) : Controller
{
    public async Task<IActionResult> Index(string? health, CancellationToken ct)
    {
        var all = await api.AssetsAsync(ct);
        var counts = new[] { "Critical", "Warning", "NoData", "Normal" }
            .ToDictionary(h => h, h => all.Count(a => a.Health == h));
        counts["All"] = all.Count;
        var filtered = string.IsNullOrEmpty(health) ? all : all.Where(a => a.Health == health).ToList();
        return View(new AssetsIndexModel
        {
            Assets = filtered.OrderBy(a => HealthRank(a.Health)).ThenBy(a => a.Name).ToList(),
            Counts = counts,
            Health = health,
        });
    }

    [HttpGet("Assets/Details/{id:int}")]
    public async Task<IActionResult> Details(int id, CancellationToken ct)
    {
        var asset = await api.AssetAsync(id, ct);
        var alerts = await api.AlertsAsync(new AlertFilter(null, false, null, id), 1, 10, ct);
        var summary = (await api.AssetsAsync(ct)).FirstOrDefault(a => a.Id == id);
        return View(new AssetDetailModel { Asset = asset, Alerts = alerts, Health = summary?.Health ?? "Normal" });
    }

    /// <summary>Grafik verisi (tarayıcıdaki Chart.js çağırır). 24 saat ham, 7 ve 30 gün saatlik özet.</summary>
    [HttpGet("Assets/{id:int}/Series")]
    public async Task<IActionResult> Series(int id, string code, string range = "24h", CancellationToken ct = default)
    {
        var (span, bucket) = range switch
        {
            "7d" => (TimeSpan.FromDays(7), "Hour"),
            "30d" => (TimeSpan.FromDays(30), "Hour"),
            _ => (TimeSpan.FromHours(24), "Raw"),
        };
        var to = clock.GetUtcNow().UtcDateTime;
        SeriesResult data;
        try
        {
            data = await api.SeriesAsync(id, code, to - span, to, bucket, ct);
        }
        catch (ApiException ex) when (bucket == "Raw" && ex.Status == System.Net.HttpStatusCode.BadRequest)
        {
            // Çok sık ölçüm yapan sensörde ham veri API sınırını aşar (400): saatlik özete düşülür.
            bucket = "Hour";
            data = await api.SeriesAsync(id, code, to - span, to, bucket, ct);
        }
        return Json(new
        {
            bucket,
            points = data.Points.Select(p => new { t = p.Time, min = p.Min, avg = p.Avg, max = p.Max, count = p.Count }),
        });
    }

    [Authorize(Roles = "Admin")]
    [HttpGet]
    public IActionResult Create() => View(new CreateAssetModel());

    [Authorize(Roles = "Admin")]
    [HttpPost]
    public async Task<IActionResult> Create(CreateAssetModel model, CancellationToken ct)
    {
        if (!ModelState.IsValid) return View(model);
        try
        {
            var created = await api.CreateAssetAsync(model.Code.Trim(), model.Name.Trim(), model.Category, model.Location?.Trim(), ct);
            return View("Created", created); // API anahtarı yalnızca bu sayfada, bir kez gösterilir
        }
        catch (ApiException ex) when (ex.Status is System.Net.HttpStatusCode.Conflict or System.Net.HttpStatusCode.BadRequest)
        {
            ModelState.AddModelError("", ex.Message);
            return View(model);
        }
    }

    [Authorize(Roles = "Admin")]
    [HttpPost("Assets/{id:int}/Sensors")]
    public async Task<IActionResult> AddSensor(int id, string code, string name, string unit, CancellationToken ct)
        => await Mutate(id, () => api.AddSensorAsync(id, code?.Trim() ?? "", name?.Trim() ?? "", unit?.Trim() ?? "", ct), "Sensör eklendi.");

    [Authorize(Roles = "Admin")]
    [HttpPost("Assets/{id:int}/Rules")]
    public async Task<IActionResult> AddRule(int id, int sensorId, string kind, decimal threshold, decimal? hysteresis, int? consecutiveCount, string severity, CancellationToken ct)
        => await Mutate(id, () => api.AddRuleAsync(sensorId, kind, threshold, hysteresis ?? 0, consecutiveCount ?? 1, severity, ct), "Alarm kuralı eklendi.");

    [Authorize(Roles = "Admin")]
    [HttpPost("Assets/{id:int}/RotateKey")]
    public async Task<IActionResult> RotateKey(int id, CancellationToken ct)
    {
        var key = await api.RotateKeyAsync(id, ct);
        return View("KeyRotated", key);
    }

    private async Task<IActionResult> Mutate(int id, Func<Task> action, string successMessage)
    {
        try
        {
            await action();
            TempData["Success"] = successMessage;
        }
        catch (ApiException ex) when (ex.Status is System.Net.HttpStatusCode.Conflict or System.Net.HttpStatusCode.BadRequest or System.Net.HttpStatusCode.NotFound)
        {
            TempData["Error"] = ex.Message;
        }
        return RedirectToAction(nameof(Details), new { id });
    }

    private static int HealthRank(string health) => health switch { "Critical" => 0, "Warning" => 1, "NoData" => 2, _ => 3 };
}
