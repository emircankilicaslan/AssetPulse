using Dapper;

namespace AssetPulse.Api.Data;

/// <summary>Eski ölçümlerin temizliği (saklama süresi).</summary>
public sealed class ReadingRetentionStore(Db db)
{
    /// <summary>
    /// <paramref name="cutoffUtc"/> tarihinden eski ölçümleri parça parça siler ve silinen toplam satır sayısını döndürür.
    /// Her sensörün <b>en son</b> ölçümü yaşı ne olursa olsun korunur: ölçüm göndermeyi bırakan bir cihazın son
    /// değeri ve zamanı ("Veri yok" durumunun dayanağı) panelde görünmeye devam eder.
    /// Silme küçük gruplar halinde yapılır; böylece uzun süreli kilit ve büyük işlem günlüğü oluşmaz.
    /// </summary>
    public async Task<int> PurgeAsync(DateTime cutoffUtc, int batchSize, CancellationToken ct)
    {
        if (batchSize < 1) throw new ArgumentOutOfRangeException(nameof(batchSize));

        const string sql =
            """
            DELETE TOP (@batchSize) r
            FROM dbo.Readings AS r
            WHERE r.MeasuredAt < @cutoff
              AND r.MeasuredAt < (SELECT MAX(x.MeasuredAt) FROM dbo.Readings AS x WHERE x.SensorId = r.SensorId)
            """;

        var toplam = 0;
        await using var conn = await db.OpenAsync(ct);
        while (true)
        {
            var silinen = await conn.ExecuteAsync(new CommandDefinition(sql, new { batchSize, cutoff = cutoffUtc }, cancellationToken: ct));
            toplam += silinen;
            if (silinen < batchSize) return toplam;
        }
    }
}
