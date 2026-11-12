namespace AssetPulse.Web.Api;

// API'nin JSON yanıtlarının arayüz tarafındaki karşılıkları. Sıralı değerler (sağlık, önem, durum) API'deki
// gibi metin olarak taşınır; Türkçe etiketlere dönüşüm Helpers/Display.cs içindedir.

public sealed record LoginResult(string Token, DateTime ExpiresAt, string Username, string Role);

public sealed record AssetSummary(
    int Id, string Code, string Name, string Category, string? Location,
    string Health, int OpenAlerts, DateTime? LastReadingAt);

public sealed record RuleItem(
    int Id, int SensorId, string Kind, decimal Threshold, decimal Hysteresis,
    int ConsecutiveCount, string Severity, bool IsActive);

public sealed record SensorItem(
    int Id, string Code, string Name, string Unit, DateTime? LastMeasuredAt, decimal? LastValue,
    List<RuleItem> Rules);

public sealed record AssetDetail(
    int Id, string Code, string Name, string Category, string? Location, bool IsActive, DateTime CreatedAt,
    List<SensorItem> Sensors);

public sealed record AlertItem(
    long Id, int AssetId, string AssetCode, string AssetName, int SensorId, string SensorCode, string Unit,
    string Kind, decimal Threshold, string Severity, string Status, DateTime OpenedAt, decimal OpenValue,
    string? AcknowledgedBy, DateTime? AcknowledgedAt, DateTime? ClearedAt, string? Note);

public sealed record AlertPage(List<AlertItem> Items, int Total, int Page, int PageSize)
{
    public int TotalPages => Math.Max(1, (int)Math.Ceiling(Total / (double)Math.Max(1, PageSize)));
}

public sealed record AssetTotals(int Total, Dictionary<string, int> ByHealth);

public sealed record Dashboard(
    AssetTotals Assets, Dictionary<string, int> OpenAlerts, List<AlertItem> LatestOpenAlerts, List<AssetSummary> Attention);

public sealed record SeriesPoint(DateTime Time, decimal Min, decimal Avg, decimal Max, int Count);

public sealed record SeriesResult(string Sensor, DateTime From, DateTime To, string Bucket, List<SeriesPoint> Points);

public sealed record CreatedAsset(int Id, string Code, string Name, string Category, string? Location, string ApiKey);

public sealed record RotatedKey(string ApiKey);

public sealed record AlertFilter(string? Status, bool OnlyOpen, string? Severity, int? AssetId);
