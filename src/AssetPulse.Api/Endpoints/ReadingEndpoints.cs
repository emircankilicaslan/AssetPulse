using AssetPulse.Api.Data;
using AssetPulse.Api.Security;
using AssetPulse.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace AssetPulse.Api.Endpoints;

public sealed record IngestRequest(List<ReadingInput>? Readings);

public static class ReadingEndpoints
{
    public const string ApiKeyHeader = "X-Api-Key";
    public const int MaxRangeDays = 31;
    public const int MaxRawRangeDays = 3;

    public static void Map(IEndpointRouteBuilder app)
    {
        // Cihaz uç noktası: JWT yerine varlığa özel API anahtarı kullanır.
        app.MapPost("/api/readings", async (
                IngestRequest req, [FromHeader(Name = ApiKeyHeader)] string? apiKey,
                AssetStore assets, IngestionService ingestion, CancellationToken ct) =>
            {
                if (string.IsNullOrWhiteSpace(apiKey))
                    return Results.Problem(title: "Kimlik doğrulanamadı", detail: $"{ApiKeyHeader} başlığı zorunludur.", statusCode: StatusCodes.Status401Unauthorized);
                var assetId = await assets.FindIdByApiKeyHashAsync(ApiKeys.Hash(apiKey.Trim()), ct);
                if (assetId is null)
                    return Results.Problem(title: "Kimlik doğrulanamadı", detail: "API anahtarı geçersiz.", statusCode: StatusCodes.Status401Unauthorized);

                if (req.Readings is null || req.Readings.Count == 0)
                    return Helpers.Invalid("readings", "En az bir ölçüm gönderilmelidir.");
                if (req.Readings.Count > IngestionService.MaxBatch)
                    return Helpers.Invalid("readings", $"Tek istekte en fazla {IngestionService.MaxBatch} ölçüm gönderilebilir.");

                return Results.Ok(await ingestion.IngestAsync(assetId.Value, req.Readings, ct));
            })
            .AllowAnonymous()
            .WithTags("Ölçümler")
            .WithSummary("Cihazdan toplu ölçüm alır (X-Api-Key). Aynı sensör+zaman tekrar gönderilirse yok sayılır.");

        app.MapGet("/api/assets/{id:int}/sensors/{code}/series", async (
                int id, string code, DateTime? from, DateTime? to, Bucket? bucket,
                AssetStore assets, SeriesStore series, TimeProvider clock, IOptions<PulseOptions> options, CancellationToken ct) =>
            {
                var end = (to ?? clock.GetUtcNow().UtcDateTime).ToUniversalTime();
                var start = (from ?? end.AddHours(-24)).ToUniversalTime();
                var kind = bucket ?? Bucket.Hour;
                if (start >= end) return Helpers.Invalid("from", "'from' değeri 'to' değerinden önce olmalıdır.");
                var limit = TimeSpan.FromDays(kind == Bucket.Raw ? MaxRawRangeDays : MaxRangeDays);
                if (end - start > limit)
                    return Helpers.Invalid("to", $"{kind} için aralık en fazla {limit.TotalDays:0} gün olabilir.");

                var sensorId = await assets.FindSensorIdAsync(id, code, ct);
                if (sensorId is null) return Helpers.NotFound("Sensör bulunamadı.");
                var points = await series.QueryAsync(sensorId.Value, start, end, kind, options.Value.MaxRawSeriesPoints + 1, ct);
                    if (kind == Bucket.Raw && points.Count > options.Value.MaxRawSeriesPoints)
                        return Helpers.Invalid("to", $"Ham veri en fazla {options.Value.MaxRawSeriesPoints} nokta döndürür; aralığı daraltın veya saatlik/günlük kovayı kullanın.");
                return Results.Ok(new { sensor = code, from = start, to = end, bucket = kind, points });
            })
            .RequireAuthorization()
            .WithTags("Ölçümler")
            .WithSummary("Sensör zaman serisi: ham, saatlik veya günlük (min/ortalama/maks). Varsayılan: son 24 saat, saatlik.");
    }
}
