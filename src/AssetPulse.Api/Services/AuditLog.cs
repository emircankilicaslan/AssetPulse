using AssetPulse.Api.Data;

namespace AssetPulse.Api.Services;

/// <summary>
/// Başarılı bir işlemden sonra denetim kaydı yazar. Kayıt yazılamazsa işlem geri alınmaz (örneğin yenilenen
/// cihaz anahtarı kaybolmasın diye); hata günlüğe yazılır. Parola ve API anahtarı hiçbir zaman kayda girmez.
/// </summary>
public sealed class AuditLog(AuditStore store, TimeProvider clock, ILogger<AuditLog> logger)
{
    public Task RecordAsync(HttpContext http, string action, string? entityType, object? entityId, string? details = null) =>
        WriteAsync(http.User.FindFirst("name")?.Value ?? "bilinmiyor", http.User.FindFirst("role")?.Value,
            action, entityType, entityId, details, http.RequestAborted);

    public Task RecordAsActorAsync(string actor, string? role, string action, string? entityType = null, object? entityId = null,
        string? details = null, CancellationToken ct = default) =>
        WriteAsync(actor, role, action, entityType, entityId, details, ct);

    private async Task WriteAsync(string actor, string? role, string action, string? entityType, object? entityId,
        string? details, CancellationToken ct)
    {
        try
        {
            await store.WriteAsync(clock.GetUtcNow().UtcDateTime, Trim(actor, 60)!, Trim(role, 20), action,
                Trim(entityType, 20), Trim(entityId?.ToString(), 40), Trim(details, 500), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Denetim kaydı yazılamadı: {Action} {EntityType} {EntityId}", action, entityType, entityId);
        }
    }

    private static string? Trim(string? value, int max) =>
        value is null ? null : value.Length <= max ? value : value[..max];
}
