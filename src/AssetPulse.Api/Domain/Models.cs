namespace AssetPulse.Api.Domain;

public enum AssetCategory : byte { Transformer = 0, Lighting = 1, Signalling = 2, Pump = 3, Generator = 4, Other = 5 }

public enum RuleKind : byte { UpperLimit = 0, LowerLimit = 1 }

public enum Severity : byte { Info = 0, Warning = 1, Critical = 2 }

public enum AlertStatus : byte { Active = 0, Acknowledged = 1, Cleared = 2 }

public enum Health { Normal, Warning, Critical, NoData }

/// <summary>Bir sensöre bağlı alarm kuralı ve o anki ihlal sayacı.</summary>
public sealed record AlertRule(
    int Id, int SensorId, RuleKind Kind, decimal Threshold, decimal Hysteresis,
    int ConsecutiveCount, Severity Severity, int ViolationStreak);
