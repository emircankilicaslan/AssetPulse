using AssetPulse.Api.Domain;
using Dapper;
using Microsoft.Data.SqlClient;

namespace AssetPulse.Api.Data;

public sealed class AssetStore(Db db)
{
    /// <summary>Benzersizlik ihlali (aynı kod) için SQL Server hata numaraları.</summary>
    public static bool IsDuplicate(SqlException ex) => ex.Number is 2627 or 2601;

    public async Task<int> CreateAsync(string code, string name, AssetCategory category, string? location, string apiKeyHash, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            """
            INSERT INTO dbo.Assets (Code, Name, Category, Location, ApiKeyHash)
            OUTPUT INSERTED.Id
            VALUES (@code, @name, @category, @location, @apiKeyHash)
            """,
            new { code, name, category, location, apiKeyHash }, cancellationToken: ct));
    }

    public async Task<AssetRow?> GetAsync(int id, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<AssetRow>(new CommandDefinition(
            "SELECT Id, Code, Name, Category, Location, IsActive, CreatedAt FROM dbo.Assets WHERE Id = @id",
            new { id }, cancellationToken: ct));
    }

    /// <summary>Cihazın gönderdiği anahtarın özetinden aktif varlığı bulur.</summary>
    public async Task<int?> FindIdByApiKeyHashAsync(string hash, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<int?>(new CommandDefinition(
            "SELECT Id FROM dbo.Assets WHERE ApiKeyHash = @hash AND IsActive = 1", new { hash }, cancellationToken: ct));
    }

    public async Task<bool> RotateKeyAsync(int id, string apiKeyHash, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE dbo.Assets SET ApiKeyHash = @apiKeyHash WHERE Id = @id", new { id, apiKeyHash }, cancellationToken: ct)) == 1;
    }

    /// <summary>Varlığı pasife alır / yeniden etkinleştirir. Pasif varlığın cihaz anahtarı geçersiz olur ve filo listesinden çıkar; geçmiş veri korunur. İdempotenttir.</summary>
    public async Task<bool> SetActiveAsync(int id, bool isActive, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE dbo.Assets SET IsActive = @isActive WHERE Id = @id", new { id, isActive }, cancellationToken: ct)) == 1;
    }

    /// <summary>Aktif varlıklar; son ölçüm zamanı ve açık alarm özeti ile birlikte.</summary>
    public async Task<IReadOnlyList<FleetRow>> FleetAsync(CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var rows = await conn.QueryAsync<FleetRow>(new CommandDefinition(
            """
            SELECT a.Id, a.Code, a.Name, a.Category, a.Location,
                   lr.LastReadingAt, al.OpenAlerts, al.MaxSeverity
            FROM dbo.Assets a
            OUTER APPLY (
                SELECT MAX(m.LastAt) AS LastReadingAt
                FROM dbo.Sensors s
                CROSS APPLY (SELECT MAX(r.MeasuredAt) AS LastAt FROM dbo.Readings r WHERE r.SensorId = s.Id) m
                WHERE s.AssetId = a.Id
            ) lr
            OUTER APPLY (
                SELECT COUNT(*) AS OpenAlerts, CAST(ISNULL(MAX(x.Severity), 0) AS TINYINT) AS MaxSeverity
                FROM dbo.Alerts x
                WHERE x.AssetId = a.Id AND x.Status IN (0, 1)
            ) al
            WHERE a.IsActive = 1
            ORDER BY a.Name
            """, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<int> AddSensorAsync(int assetId, string code, string name, string unit, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            """
            INSERT INTO dbo.Sensors (AssetId, Code, Name, Unit)
            OUTPUT INSERTED.Id
            VALUES (@assetId, @code, @name, @unit)
            """, new { assetId, code, name, unit }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<SensorRow>> SensorsAsync(int assetId, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var rows = await conn.QueryAsync<SensorRow>(new CommandDefinition(
            """
            SELECT s.Id, s.AssetId, s.Code, s.Name, s.Unit, lr.MeasuredAt AS LastMeasuredAt, lr.Value AS LastValue
            FROM dbo.Sensors s
            OUTER APPLY (
                SELECT TOP (1) r.MeasuredAt, r.Value FROM dbo.Readings r
                WHERE r.SensorId = s.Id ORDER BY r.MeasuredAt DESC
            ) lr
            WHERE s.AssetId = @assetId
            ORDER BY s.Code
            """, new { assetId }, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<int?> FindSensorIdAsync(int assetId, string code, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<int?>(new CommandDefinition(
            "SELECT Id FROM dbo.Sensors WHERE AssetId = @assetId AND Code = @code",
            new { assetId, code }, cancellationToken: ct));
    }

    public async Task<int?> SensorAssetIdAsync(int sensorId, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<int?>(new CommandDefinition(
            "SELECT AssetId FROM dbo.Sensors WHERE Id = @sensorId", new { sensorId }, cancellationToken: ct));
    }

    public async Task<int> AddRuleAsync(int sensorId, RuleKind kind, decimal threshold, decimal hysteresis,
        int consecutiveCount, Severity severity, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            """
            INSERT INTO dbo.AlertRules (SensorId, Kind, Threshold, Hysteresis, ConsecutiveCount, Severity)
            OUTPUT INSERTED.Id
            VALUES (@sensorId, @kind, @threshold, @hysteresis, @consecutiveCount, @severity)
            """, new { sensorId, kind, threshold, hysteresis, consecutiveCount, severity }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<RuleRow>> RulesAsync(int assetId, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var rows = await conn.QueryAsync<RuleRow>(new CommandDefinition(
            """
            SELECT r.Id, r.SensorId, r.Kind, r.Threshold, r.Hysteresis, r.ConsecutiveCount, r.Severity, r.IsActive
            FROM dbo.AlertRules r JOIN dbo.Sensors s ON s.Id = r.SensorId
            WHERE s.AssetId = @assetId ORDER BY r.Id
            """, new { assetId }, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<bool> AnyAsync(CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<int>(new CommandDefinition("SELECT COUNT(*) FROM dbo.Assets", cancellationToken: ct)) > 0;
    }
}
