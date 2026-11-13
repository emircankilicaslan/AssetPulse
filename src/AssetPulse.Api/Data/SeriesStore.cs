using Dapper;

namespace AssetPulse.Api.Data;

public enum Bucket { Raw, Hour, Day }

public sealed class SeriesStore(Db db)
{
    /// <summary>
    /// Bir sensörün [from, to) aralığındaki ölçümleri; saatlik/günlük için T-SQL ile min/ortalama/maks özeti.
    /// Kova ifadesi kullanıcı girdisinden değil, sabit bir listeden seçilir (SQL enjeksiyonu olmaz).
    /// </summary>
    public async Task<IReadOnlyList<SeriesPoint>> QueryAsync(int sensorId, DateTime from, DateTime to, Bucket bucket, int maxRows, CancellationToken ct)
    {
        var sql = bucket switch
        {
            Bucket.Raw =>
                """
                SELECT TOP (@maxRows) MeasuredAt AS Time, Value AS Min, Value AS Avg, Value AS Max, 1 AS Count
                FROM dbo.Readings
                WHERE SensorId = @sensorId AND MeasuredAt >= @from AND MeasuredAt < @to
                ORDER BY MeasuredAt
                """,
            Bucket.Hour => Aggregate("HOUR"),
            Bucket.Day => Aggregate("DAY"),
            _ => throw new ArgumentOutOfRangeException(nameof(bucket)),
        };
        await using var conn = await db.OpenAsync(ct);
        var rows = await conn.QueryAsync<SeriesPoint>(new CommandDefinition(sql, new { sensorId, from, to, maxRows }, cancellationToken: ct));
        return rows.AsList();
    }

    private static string Aggregate(string part) =>
        $"""
        SELECT DATEADD({part}, DATEDIFF({part}, 0, MeasuredAt), 0) AS Time,
               MIN(Value) AS Min, CAST(AVG(Value) AS DECIMAL(18,4)) AS Avg, MAX(Value) AS Max, COUNT(*) AS Count
        FROM dbo.Readings
        WHERE SensorId = @sensorId AND MeasuredAt >= @from AND MeasuredAt < @to
        GROUP BY DATEADD({part}, DATEDIFF({part}, 0, MeasuredAt), 0)
        ORDER BY Time
        """;
}
