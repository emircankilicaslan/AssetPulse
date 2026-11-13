using AssetPulse.Api.Data;
using AssetPulse.Api.Domain;
using AssetPulse.Api.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace AssetPulse.Api.Endpoints;

public sealed class PulseOptions
{
    /// <summary>Bu süre boyunca ölçüm gelmeyen varlık "Veri yok" sayılır.</summary>
    public int StaleAfterMinutes { get; set; } = 30;

    /// <summary>Ham (Raw) zaman serisi yanıtında dönecek en fazla nokta sayısı; aşılırsa istek 400 ile reddedilir.</summary>
    public int MaxRawSeriesPoints { get; set; } = 20_000;

    /// <summary>
    /// Ölçümlerin saklanacağı gün sayısı; daha eskileri düzenli olarak silinir (her sensörün son ölçümü hariç).
    /// 0 (varsayılan) saklama sınırı olmadığı, hiçbir ölçümün silinmeyeceği anlamına gelir.
    /// </summary>
    public int RetentionDays { get; set; } = 0;

    /// <summary>Saklama temizliğinin kaç dakikada bir çalışacağı.</summary>
    public int RetentionIntervalMinutes { get; set; } = 360;
}

public sealed record CreateAssetRequest(string? Code, string? Name, AssetCategory Category, string? Location);
public sealed record CreateSensorRequest(string? Code, string? Name, string? Unit);
public sealed record CreateRuleRequest(RuleKind Kind, decimal Threshold, decimal? Hysteresis, int? ConsecutiveCount, Severity Severity);

public sealed record AssetSummary(
    int Id, string Code, string Name, AssetCategory Category, string? Location,
    Health Health, int OpenAlerts, DateTime? LastReadingAt);

public static class AssetEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/assets").WithTags("Varlıklar");

        group.MapPost("/", async (CreateAssetRequest req, AssetStore assets, CancellationToken ct) =>
            {
                if (req.Code is null || !Helpers.CodePattern().IsMatch(req.Code))
                    return Helpers.Invalid("code", "Kod 2-40 karakter olmalı; harf, rakam, '.', '_' ve '-' içerebilir.");
                if (string.IsNullOrWhiteSpace(req.Name) || req.Name.Length > 120)
                    return Helpers.Invalid("name", "Ad 1-120 karakter olmalıdır.");
                if (!Enum.IsDefined(req.Category))
                    return Helpers.Invalid("category", "Geçersiz kategori.");
                if (req.Location is { Length: > 120 })
                    return Helpers.Invalid("location", "Konum en fazla 120 karakter olabilir.");

                var (key, hash) = ApiKeys.Generate();
                try
                {
                    var id = await assets.CreateAsync(req.Code, req.Name.Trim(), req.Category, req.Location?.Trim(), hash, ct);
                    return Results.Created($"/api/assets/{id}", new
                    {
                        id, code = req.Code, name = req.Name.Trim(), category = req.Category, location = req.Location?.Trim(),
                        apiKey = key,
                        apiKeyNote = "Anahtar yalnızca şimdi gösterilir; kaybolursa yenisi üretilmelidir.",
                    });
                }
                catch (SqlException ex) when (AssetStore.IsDuplicate(ex))
                {
                    return Helpers.Conflict($"'{req.Code}' kodlu bir varlık zaten var.");
                }
            })
            .RequireAuthorization("AdminOnly")
            .WithSummary("Varlık oluşturur ve cihaz için API anahtarı üretir (yalnızca Admin).");

        group.MapGet("/", async (Health? health, AssetStore assets, IOptions<PulseOptions> options, TimeProvider clock, CancellationToken ct) =>
            {
                var summaries = await Fleet(assets, options.Value, clock, ct);
                return Results.Ok(health is null ? summaries : summaries.Where(a => a.Health == health));
            })
            .RequireAuthorization()
            .WithSummary("Aktif varlıkları sağlık durumuyla listeler (?health=Critical|Warning|NoData|Normal).");

        group.MapGet("/{id:int}", async (int id, AssetStore assets, CancellationToken ct) =>
            {
                var asset = await assets.GetAsync(id, ct);
                if (asset is null) return Helpers.NotFound("Varlık bulunamadı.");
                var sensors = await assets.SensorsAsync(id, ct);
                var rules = await assets.RulesAsync(id, ct);
                return Results.Ok(new
                {
                    asset.Id, asset.Code, asset.Name, asset.Category, asset.Location, asset.IsActive, asset.CreatedAt,
                    sensors = sensors.Select(s => new
                    {
                        s.Id, s.Code, s.Name, s.Unit, s.LastMeasuredAt, s.LastValue,
                        rules = rules.Where(r => r.SensorId == s.Id),
                    }),
                });
            })
            .RequireAuthorization()
            .WithSummary("Varlığı, sensörlerini, son değerlerini ve alarm kurallarını getirir.");

        group.MapPost("/{id:int}/rotate-key", async (int id, AssetStore assets, CancellationToken ct) =>
            {
                var (key, hash) = ApiKeys.Generate();
                return await assets.RotateKeyAsync(id, hash, ct)
                    ? Results.Ok(new { apiKey = key, apiKeyNote = "Eski anahtar geçersiz oldu." })
                    : Helpers.NotFound("Varlık bulunamadı.");
            })
            .RequireAuthorization("AdminOnly")
            .WithSummary("Cihaz anahtarını yeniler; eskisi hemen geçersiz olur (yalnızca Admin).");

        group.MapPost("/{id:int}/deactivate", async (int id, AssetStore assets, CancellationToken ct) =>
            await assets.SetActiveAsync(id, false, ct) ? Results.NoContent() : Helpers.NotFound("Varlık bulunamadı."))
            .RequireAuthorization("AdminOnly")
            .WithSummary("Varlığı pasife alır: cihaz anahtarı geçersiz olur, filo listesinden çıkar; geçmiş veri korunur (yalnızca Admin).");

        group.MapPost("/{id:int}/activate", async (int id, AssetStore assets, CancellationToken ct) =>
            await assets.SetActiveAsync(id, true, ct) ? Results.NoContent() : Helpers.NotFound("Varlık bulunamadı."))
            .RequireAuthorization("AdminOnly")
            .WithSummary("Pasife alınmış varlığı yeniden etkinleştirir (yalnızca Admin).");

        group.MapPost("/{id:int}/sensors", async (int id, CreateSensorRequest req, AssetStore assets, CancellationToken ct) =>
            {
                if (req.Code is null || !Helpers.CodePattern().IsMatch(req.Code))
                    return Helpers.Invalid("code", "Kod 2-40 karakter olmalı; harf, rakam, '.', '_' ve '-' içerebilir.");
                if (string.IsNullOrWhiteSpace(req.Name) || req.Name.Length > 120)
                    return Helpers.Invalid("name", "Ad 1-120 karakter olmalıdır.");
                if (string.IsNullOrWhiteSpace(req.Unit) || req.Unit.Length > 16)
                    return Helpers.Invalid("unit", "Birim 1-16 karakter olmalıdır.");
                if (await assets.GetAsync(id, ct) is null) return Helpers.NotFound("Varlık bulunamadı.");
                try
                {
                    var sensorId = await assets.AddSensorAsync(id, req.Code, req.Name.Trim(), req.Unit.Trim(), ct);
                    return Results.Created($"/api/assets/{id}", new { id = sensorId, code = req.Code, name = req.Name.Trim(), unit = req.Unit.Trim() });
                }
                catch (SqlException ex) when (AssetStore.IsDuplicate(ex))
                {
                    return Helpers.Conflict($"Bu varlıkta '{req.Code}' kodlu sensör zaten var.");
                }
            })
            .RequireAuthorization("AdminOnly")
            .WithSummary("Varlığa sensör ekler (yalnızca Admin).");

        app.MapPost("/api/sensors/{sensorId:int}/rules", async (int sensorId, CreateRuleRequest req, AssetStore assets, CancellationToken ct) =>
            {
                if (!Enum.IsDefined(req.Kind)) return Helpers.Invalid("kind", "Geçersiz kural türü.");
                if (!Enum.IsDefined(req.Severity)) return Helpers.Invalid("severity", "Geçersiz önem derecesi.");
                var hysteresis = req.Hysteresis ?? 0m;
                var consecutive = req.ConsecutiveCount ?? 1;
                if (hysteresis < 0) return Helpers.Invalid("hysteresis", "Histerezis negatif olamaz.");
                if (consecutive is < 1 or > 1000) return Helpers.Invalid("consecutiveCount", "Art arda ihlal sayısı 1-1000 olmalıdır.");
                if (Math.Abs(req.Threshold) >= 1e13m || hysteresis >= 1e13m) return Helpers.Invalid("threshold", "Değer aralık dışında.");
                if (await assets.SensorAssetIdAsync(sensorId, ct) is null) return Helpers.NotFound("Sensör bulunamadı.");

                var id = await assets.AddRuleAsync(sensorId, req.Kind, req.Threshold, hysteresis, consecutive, req.Severity, ct);
                return Results.Created($"/api/sensors/{sensorId}/rules/{id}", new
                {
                    id, sensorId, req.Kind, req.Threshold, hysteresis, consecutiveCount = consecutive, req.Severity,
                });
            })
            .RequireAuthorization("AdminOnly")
            .WithTags("Varlıklar")
            .WithSummary("Sensöre alarm kuralı ekler (yalnızca Admin).");
    }

    internal static async Task<List<AssetSummary>> Fleet(AssetStore assets, PulseOptions options, TimeProvider clock, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var stale = TimeSpan.FromMinutes(options.StaleAfterMinutes);
        var rows = await assets.FleetAsync(ct);
        return rows.Select(r => new AssetSummary(
            r.Id, r.Code, r.Name, r.Category, r.Location,
            RuleEngine.ComputeHealth(r.OpenAlerts, r.MaxSeverity, r.LastReadingAt, now, stale),
            r.OpenAlerts, r.LastReadingAt)).ToList();
    }
}
