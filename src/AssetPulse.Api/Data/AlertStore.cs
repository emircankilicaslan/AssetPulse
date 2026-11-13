using AssetPulse.Api.Domain;
using Dapper;
using System.Data;

namespace AssetPulse.Api.Data;

public sealed record AlertFilter(AlertStatus? Status, bool OnlyOpen, Severity? Severity, int? AssetId, DateTime? From = null, DateTime? To = null);

public sealed record AlertPage(IReadOnlyList<AlertRow> Items, int Total, int Page, int PageSize);

public enum AckOutcome { Done, NotFound, NotActive }

public sealed class AlertStore(Db db)
{
    private const string Select =
        """
        SELECT al.Id, al.AssetId, a.Code AS AssetCode, a.Name AS AssetName, al.SensorId, s.Code AS SensorCode, s.Unit,
               r.Kind, r.Threshold, al.Severity, al.Status, al.OpenedAt, al.OpenValue,
               al.AcknowledgedBy, al.AcknowledgedAt, al.ClearedAt, al.Note
        FROM dbo.Alerts al
        JOIN dbo.Assets a ON a.Id = al.AssetId
        JOIN dbo.Sensors s ON s.Id = al.SensorId
        JOIN dbo.AlertRules r ON r.Id = al.RuleId
        """;

    private static (string Where, DynamicParameters Args) Build(AlertFilter f)
    {
        var conditions = new List<string>();
        var args = new DynamicParameters();
        if (f.Status is { } status) { conditions.Add("al.Status = @status"); args.Add("status", (byte)status); }
        else if (f.OnlyOpen) conditions.Add("al.Status IN (0, 1)");
        if (f.Severity is { } sev) { conditions.Add("al.Severity = @severity"); args.Add("severity", (byte)sev); }
        if (f.AssetId is { } assetId) { conditions.Add("al.AssetId = @assetId"); args.Add("assetId", assetId); }
        if (f.From is { } fromUtc) { conditions.Add("al.OpenedAt >= @fromUtc"); args.Add("fromUtc", fromUtc, DbType.DateTime2); }
        if (f.To is { } toUtc) { conditions.Add("al.OpenedAt < @toUtc"); args.Add("toUtc", toUtc, DbType.DateTime2); }
        return (conditions.Count == 0 ? "" : "WHERE " + string.Join(" AND ", conditions), args);
    }

    public async Task<AlertPage> ListAsync(AlertFilter filter, int page, int pageSize, CancellationToken ct)
    {
        var (where, args) = Build(filter);
        args.Add("skip", (page - 1) * pageSize);
        args.Add("take", pageSize);
        await using var conn = await db.OpenAsync(ct);
        var total = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            $"SELECT COUNT(*) FROM dbo.Alerts al {where}", args, cancellationToken: ct));
        var items = await conn.QueryAsync<AlertRow>(new CommandDefinition(
            $"{Select} {where} ORDER BY al.OpenedAt DESC, al.Id DESC OFFSET @skip ROWS FETCH NEXT @take ROWS ONLY",
            args, cancellationToken: ct));
        return new AlertPage(items.AsList(), total, page, pageSize);
    }

    public async Task<AlertRow?> GetAsync(long id, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<AlertRow>(new CommandDefinition(
            $"{Select} WHERE al.Id = @id", new { id }, cancellationToken: ct));
    }

    /// <summary>Alarmı üstlenir. Yalnızca "Aktif" alarm üstlenilebilir; yarış durumunda tek istek başarılı olur.</summary>
    public async Task<AckOutcome> AcknowledgeAsync(long id, string user, string? note, DateTime nowUtc, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var updated = await conn.ExecuteAsync(new CommandDefinition(
            """
            UPDATE dbo.Alerts
            SET Status = 1, AcknowledgedBy = @user, AcknowledgedAt = @nowUtc, Note = @note
            WHERE Id = @id AND Status = 0
            """, new { id, user, nowUtc, note }, cancellationToken: ct));
        if (updated == 1) return AckOutcome.Done;
        var exists = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM dbo.Alerts WHERE Id = @id", new { id }, cancellationToken: ct));
        return exists == 0 ? AckOutcome.NotFound : AckOutcome.NotActive;
    }

    /// <summary>Açık alarm sayıları (önem derecesine göre).</summary>
    public async Task<IReadOnlyDictionary<Severity, int>> OpenCountsAsync(CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var rows = await conn.QueryAsync<(byte Severity, int Count)>(new CommandDefinition(
            "SELECT Severity, COUNT(*) FROM dbo.Alerts WHERE Status IN (0, 1) GROUP BY Severity", cancellationToken: ct));
        var map = Enum.GetValues<Severity>().ToDictionary(s => s, _ => 0);
        foreach (var (severity, count) in rows) map[(Severity)severity] = count;
        return map;
    }
}
