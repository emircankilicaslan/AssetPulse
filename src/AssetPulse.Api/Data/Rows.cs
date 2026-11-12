using AssetPulse.Api.Domain;

namespace AssetPulse.Api.Data;

public sealed record UserRow(int Id, string Username, string PasswordHash, string Role);

public sealed record AssetRow(
    int Id, string Code, string Name, AssetCategory Category, string? Location, bool IsActive, DateTime CreatedAt);

public sealed record FleetRow(
    int Id, string Code, string Name, AssetCategory Category, string? Location,
    DateTime? LastReadingAt, int OpenAlerts, Severity MaxSeverity);

public sealed record SensorRow(
    int Id, int AssetId, string Code, string Name, string Unit, DateTime? LastMeasuredAt, decimal? LastValue);

public sealed record RuleRow(
    int Id, int SensorId, RuleKind Kind, decimal Threshold, decimal Hysteresis,
    int ConsecutiveCount, Severity Severity, bool IsActive);

public sealed record AlertRow(
    long Id, int AssetId, string AssetCode, string AssetName, int SensorId, string SensorCode, string Unit,
    RuleKind Kind, decimal Threshold, Severity Severity, AlertStatus Status, DateTime OpenedAt, decimal OpenValue,
    string? AcknowledgedBy, DateTime? AcknowledgedAt, DateTime? ClearedAt, string? Note);

public sealed record SeriesPoint(DateTime Time, decimal Min, decimal Avg, decimal Max, int Count);
