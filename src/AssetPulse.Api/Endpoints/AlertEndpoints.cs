using System.Globalization;
using System.Text;
using AssetPulse.Api.Data;
using AssetPulse.Api.Domain;
using Microsoft.Extensions.Options;

namespace AssetPulse.Api.Endpoints;

public sealed record AckRequest(string? Note);

public static class AlertEndpoints
{
    private const int MaxExportRows = 10_000;

    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/alerts").RequireAuthorization().WithTags("Alarmlar");

        group.MapGet("/", async (AlertStatus? status, bool? open, Severity? severity, int? assetId, int? page, int? pageSize,
                AlertStore alerts, CancellationToken ct) =>
            {
                var p = page ?? 1;
                var size = pageSize ?? 25;
                if (p < 1) return Helpers.Invalid("page", "Sayfa 1 veya daha büyük olmalıdır.");
                if (size is < 1 or > 100) return Helpers.Invalid("pageSize", "Sayfa boyutu 1-100 olmalıdır.");
                var result = await alerts.ListAsync(new AlertFilter(status, open ?? false, severity, assetId), p, size, ct);
                return Results.Ok(result);
            })
            .WithSummary("Alarmları süzer ve sayfalar (?status, ?open=true, ?severity, ?assetId). En yeni önce.");

        group.MapGet("/{id:long}", async (long id, AlertStore alerts, CancellationToken ct) =>
                await alerts.GetAsync(id, ct) is { } alert ? Results.Ok(alert) : Helpers.NotFound("Alarm bulunamadı."))
            .WithSummary("Tek bir alarmı getirir.");

        group.MapPost("/{id:long}/ack", async (long id, AckRequest? req, HttpContext http, AlertStore alerts, TimeProvider clock, CancellationToken ct) =>
            {
                if (req?.Note is { Length: > 500 }) return Helpers.Invalid("note", "Not en fazla 500 karakter olabilir.");
                var user = http.User.FindFirst("name")?.Value ?? "bilinmiyor";
                var outcome = await alerts.AcknowledgeAsync(id, user, req?.Note?.Trim(), clock.GetUtcNow().UtcDateTime, ct);
                return outcome switch
                {
                    AckOutcome.Done => Results.Ok(await alerts.GetAsync(id, ct)),
                    AckOutcome.NotFound => Helpers.NotFound("Alarm bulunamadı."),
                    _ => Helpers.Conflict("Yalnızca 'Active' durumundaki alarm üstlenilebilir."),
                };
            })
            .WithSummary("Aktif alarmı üstlenir (kim ve ne zaman kaydedilir).");

        group.MapGet("/export.csv", async (AlertStatus? status, bool? open, Severity? severity, int? assetId,
                AlertStore alerts, CancellationToken ct) =>
            {
                var page = await alerts.ListAsync(new AlertFilter(status, open ?? false, severity, assetId), 1, MaxExportRows, ct);
                var sb = new StringBuilder("Id,Varlık,Sensör,Önem,Durum,Açılış (UTC),Tetikleyen değer,Birim,Eşik,Üstlenen,Kapanış (UTC)\n");
                foreach (var a in page.Items)
                {
                    sb.Append(string.Join(',',
                        a.Id.ToString(CultureInfo.InvariantCulture), Helpers.CsvCell(a.AssetName), Helpers.CsvCell(a.SensorCode),
                        a.Severity, a.Status, a.OpenedAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                        a.OpenValue.ToString(CultureInfo.InvariantCulture), Helpers.CsvCell(a.Unit),
                        a.Threshold.ToString(CultureInfo.InvariantCulture), Helpers.CsvCell(a.AcknowledgedBy),
                        a.ClearedAt?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? "")).Append('\n');
                }
                // Başına BOM: Excel Türkçe karakterleri doğru açsın.
                var bytes = new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
                return Results.File(bytes, "text/csv; charset=utf-8", "alarmlar.csv");
            })
            .WithSummary($"Alarmları CSV olarak indirir (en fazla {MaxExportRows} satır; hücreler formül enjeksiyonuna karşı korunur).");

        app.MapGet("/api/dashboard", async (AssetStore assets, AlertStore alerts, IOptions<PulseOptions> options, TimeProvider clock, CancellationToken ct) =>
            {
                var fleet = await AssetEndpoints.Fleet(assets, options.Value, clock, ct);
                var counts = await alerts.OpenCountsAsync(ct);
                var latest = await alerts.ListAsync(new AlertFilter(null, true, null, null), 1, 5, ct);
                return Results.Ok(new
                {
                    assets = new
                    {
                        total = fleet.Count,
                        byHealth = Enum.GetValues<Health>().ToDictionary(h => h.ToString(), h => fleet.Count(a => a.Health == h)),
                    },
                    openAlerts = counts.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value),
                    latestOpenAlerts = latest.Items,
                    attention = fleet.Where(a => a.Health != Health.Normal)
                        .OrderBy(a => a.Health == Health.Critical ? 0 : a.Health == Health.Warning ? 1 : 2).ThenBy(a => a.Name),
                });
            })
            .RequireAuthorization()
            .WithTags("Panel")
            .WithSummary("Genel durum: varlık sağlık dağılımı, açık alarm sayıları ve ilgi gerektiren varlıklar.");
    }
}
