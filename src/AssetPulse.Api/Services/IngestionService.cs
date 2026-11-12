using AssetPulse.Api.Data;
using AssetPulse.Api.Domain;
using Dapper;
using Microsoft.Data.SqlClient;

namespace AssetPulse.Api.Services;

public sealed record ReadingInput(string Sensor, DateTime MeasuredAt, decimal Value);

public sealed record IngestResult(int Received, int Accepted, int Duplicates, int Rejected, int AlertsOpened, int AlertsCleared);

/// <summary>
/// Cihazlardan gelen ölçüm toplu alımı. Tek veritabanı işleminde:
/// ölçümler kaydedilir (aynı sensör+zaman ikinci kez eklenmez), ardından yeni ölçümler alarm kurallarına göre
/// zaman sırasıyla değerlendirilir.
/// </summary>
public sealed class IngestionService(Db db, TimeProvider clock)
{
    public const int MaxBatch = 1000;
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(30);
    public static readonly TimeSpan MaxFutureSkew = TimeSpan.FromMinutes(5);

    public async Task<IngestResult> IngestAsync(int assetId, IReadOnlyList<ReadingInput> input, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        await using var conn = await db.OpenAsync(ct);

        var sensorIds = (await conn.QueryAsync<(int Id, string Code)>(new CommandDefinition(
                "SELECT Id, Code FROM dbo.Sensors WHERE AssetId = @assetId", new { assetId }, cancellationToken: ct)))
            .ToDictionary(s => s.Code, s => s.Id, StringComparer.OrdinalIgnoreCase);

        var rejected = 0;
        var valid = new List<(int SensorId, DateTime At, decimal Value)>();
        foreach (var item in input)
        {
            var at = Normalize(item.MeasuredAt);
            if (string.IsNullOrWhiteSpace(item.Sensor) || !sensorIds.TryGetValue(item.Sensor.Trim(), out var sensorId)
                || at < now - MaxAge || at > now + MaxFutureSkew || Math.Abs(item.Value) >= 1e13m)
            {
                rejected++;
                continue;
            }
            valid.Add((sensorId, at, item.Value));
        }

        int accepted = 0, duplicates = 0, opened = 0, cleared = 0;
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);

        foreach (var group in valid.GroupBy(v => v.SensorId))
        {
            var sensorId = group.Key;
            // Aynı sensöre eşzamanlı alımlar sıraya girsin: alarm durumu tutarlı kalır.
            await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT Id FROM dbo.Sensors WITH (UPDLOCK, ROWLOCK) WHERE Id = @sensorId",
                new { sensorId }, tx, cancellationToken: ct));
            var latest = await conn.ExecuteScalarAsync<DateTime?>(new CommandDefinition(
                "SELECT MAX(MeasuredAt) FROM dbo.Readings WHERE SensorId = @sensorId",
                new { sensorId }, tx, cancellationToken: ct));

            var fresh = new List<(DateTime At, decimal Value)>();
            var seen = new HashSet<DateTime>();
            foreach (var (_, at, value) in group.OrderBy(g => g.At))
            {
                if (!seen.Add(at)) { duplicates++; continue; }
                var inserted = await conn.ExecuteAsync(new CommandDefinition(
                    """
                    INSERT INTO dbo.Readings (SensorId, MeasuredAt, Value)
                    SELECT @sensorId, @at, @value
                    WHERE NOT EXISTS (SELECT 1 FROM dbo.Readings WHERE SensorId = @sensorId AND MeasuredAt = @at)
                    """, new { sensorId, at, value }, tx, cancellationToken: ct));
                if (inserted == 0) { duplicates++; continue; }
                accepted++;
                // Geç gelen (sensörün son ölçümünden eski) kayıtlar saklanır ama alarm değerlendirmesine girmez.
                if (latest is null || at > latest) fresh.Add((at, value));
            }

            if (fresh.Count == 0) continue;
            var (o, c) = await EvaluateAsync(conn, tx, assetId, sensorId, fresh, ct);
            opened += o;
            cleared += c;
        }

        await tx.CommitAsync(ct);
        return new IngestResult(input.Count, accepted, duplicates, rejected, opened, cleared);
    }

    private static async Task<(int Opened, int Cleared)> EvaluateAsync(
        SqlConnection conn, SqlTransaction tx, int assetId, int sensorId,
        IReadOnlyList<(DateTime At, decimal Value)> fresh, CancellationToken ct)
    {
        var rules = (await conn.QueryAsync<AlertRule>(new CommandDefinition(
            """
            SELECT Id, SensorId, Kind, Threshold, Hysteresis, ConsecutiveCount, Severity, ViolationStreak
            FROM dbo.AlertRules WHERE SensorId = @sensorId AND IsActive = 1
            """, new { sensorId }, tx, cancellationToken: ct))).AsList();
        if (rules.Count == 0) return (0, 0);

        var openRules = (await conn.QueryAsync<int>(new CommandDefinition(
            "SELECT RuleId FROM dbo.Alerts WHERE SensorId = @sensorId AND Status IN (0, 1)",
            new { sensorId }, tx, cancellationToken: ct))).ToHashSet();

        int opened = 0, cleared = 0;
        foreach (var rule in rules)
        {
            var streak = rule.ViolationStreak;
            var hasOpen = openRules.Contains(rule.Id);
            foreach (var (at, value) in fresh)
            {
                var step = RuleEngine.Next(rule, streak, hasOpen, value);
                streak = step.Streak;
                switch (step.Transition)
                {
                    case Transition.Open:
                        await conn.ExecuteAsync(new CommandDefinition(
                            """
                            INSERT INTO dbo.Alerts (RuleId, AssetId, SensorId, Severity, Status, OpenedAt, OpenValue)
                            VALUES (@ruleId, @assetId, @sensorId, @severity, 0, @at, @value)
                            """, new { ruleId = rule.Id, assetId, sensorId, severity = rule.Severity, at, value }, tx, cancellationToken: ct));
                        hasOpen = true;
                        opened++;
                        break;
                    case Transition.Clear:
                        await conn.ExecuteAsync(new CommandDefinition(
                            "UPDATE dbo.Alerts SET Status = 2, ClearedAt = @at WHERE RuleId = @ruleId AND Status IN (0, 1)",
                            new { ruleId = rule.Id, at }, tx, cancellationToken: ct));
                        hasOpen = false;
                        cleared++;
                        break;
                }
            }
            if (streak != rule.ViolationStreak)
                await conn.ExecuteAsync(new CommandDefinition(
                    "UPDATE dbo.AlertRules SET ViolationStreak = @streak WHERE Id = @id",
                    new { streak, id = rule.Id }, tx, cancellationToken: ct));
        }
        return (opened, cleared);
    }

    /// <summary>UTC'ye çevirir ve milisaniye altını keser (DATETIME2(3) ile birebir eşleşsin).</summary>
    private static DateTime Normalize(DateTime value)
    {
        var utc = value.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(value, DateTimeKind.Utc) : value.ToUniversalTime();
        return new DateTime(utc.Ticks - utc.Ticks % TimeSpan.TicksPerMillisecond, DateTimeKind.Utc);
    }
}
