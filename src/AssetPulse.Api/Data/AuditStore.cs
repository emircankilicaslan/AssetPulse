using System.Data;
using Dapper;

namespace AssetPulse.Api.Data;

public sealed record AuditRow(
    long Id, DateTime OccurredAt, string Actor, string? ActorRole, string Action,
    string? EntityType, string? EntityId, string? Details);

public sealed record AuditFilter(string? Actor, string? Action, DateTime? From, DateTime? To);

public sealed record AuditPage(IReadOnlyList<AuditRow> Items, int Total, int Page, int PageSize);

public sealed class AuditStore(Db db)
{
    public async Task WriteAsync(DateTime occurredAtUtc, string actor, string? role, string action,
        string? entityType, string? entityId, string? details, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO dbo.AuditLog (OccurredAt, Actor, ActorRole, Action, EntityType, EntityId, Details)
            VALUES (@occurredAtUtc, @actor, @role, @action, @entityType, @entityId, @details)
            """,
            new { occurredAtUtc, actor, role, action, entityType, entityId, details }, cancellationToken: ct));
    }

    private static (string Where, DynamicParameters Args) Build(AuditFilter f)
    {
        var conditions = new List<string>();
        var args = new DynamicParameters();
        if (!string.IsNullOrWhiteSpace(f.Actor)) { conditions.Add("Actor = @actor"); args.Add("actor", f.Actor.Trim()); }
        if (!string.IsNullOrWhiteSpace(f.Action)) { conditions.Add("Action = @action"); args.Add("action", f.Action.Trim()); }
        if (f.From is { } from) { conditions.Add("OccurredAt >= @fromUtc"); args.Add("fromUtc", from, DbType.DateTime2); }
        if (f.To is { } to) { conditions.Add("OccurredAt < @toUtc"); args.Add("toUtc", to, DbType.DateTime2); }
        return (conditions.Count == 0 ? "" : "WHERE " + string.Join(" AND ", conditions), args);
    }

    public async Task<AuditPage> ListAsync(AuditFilter filter, int page, int pageSize, CancellationToken ct)
    {
        var (where, args) = Build(filter);
        args.Add("skip", (page - 1) * pageSize);
        args.Add("take", pageSize);
        await using var conn = await db.OpenAsync(ct);
        var total = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            $"SELECT COUNT(*) FROM dbo.AuditLog {where}", args, cancellationToken: ct));
        var items = await conn.QueryAsync<AuditRow>(new CommandDefinition(
            $"""
            SELECT Id, OccurredAt, Actor, ActorRole, Action, EntityType, EntityId, Details
            FROM dbo.AuditLog {where}
            ORDER BY OccurredAt DESC, Id DESC OFFSET @skip ROWS FETCH NEXT @take ROWS ONLY
            """, args, cancellationToken: ct));
        return new AuditPage(items.AsList(), total, page, pageSize);
    }
}
