using AssetPulse.Api.Data;
using AssetPulse.Api.Endpoints;
using Microsoft.Extensions.Options;

namespace AssetPulse.Api.Services;

/// <summary>
/// Saklama süresinden eski ölçümleri düzenli aralıklarla siler. <c>AssetPulse:RetentionDays</c> 0 ya da negatifse
/// (varsayılan) hiçbir şey silinmez: veri kaybı yalnızca açıkça istenirse olur.
/// </summary>
public sealed class ReadingRetentionService(
    ReadingRetentionStore store, IOptions<PulseOptions> options, TimeProvider time, ILogger<ReadingRetentionService> log)
    : BackgroundService
{
    public const int BatchSize = 10_000;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var o = options.Value;
        if (o.RetentionDays <= 0)
        {
            log.LogInformation("Ölçüm saklama süresi kapalı (AssetPulse:RetentionDays <= 0); eski ölçümler silinmeyecek.");
            return;
        }

        var aralik = TimeSpan.FromMinutes(Math.Max(1, o.RetentionIntervalMinutes));
        log.LogInformation("{Gun} günden eski ölçümler {Aralik} aralıkla silinecek.", o.RetentionDays, aralik);

        using var zamanlayici = new PeriodicTimer(aralik);
        try
        {
            do
            {
                try
                {
                    var sinir = time.GetUtcNow().UtcDateTime.AddDays(-o.RetentionDays);
                    var silinen = await store.PurgeAsync(sinir, BatchSize, stoppingToken);
                    if (silinen > 0) log.LogInformation("Saklama süresini aşan {Adet} ölçüm silindi.", silinen);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    log.LogError(e, "Ölçüm temizliği başarısız oldu; bir sonraki turda yeniden denenecek.");
                }
            } while (await zamanlayici.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // Uygulama kapanıyor.
        }
    }
}
