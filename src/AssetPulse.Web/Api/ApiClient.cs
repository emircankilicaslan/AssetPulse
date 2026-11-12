using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;

namespace AssetPulse.Web.Api;

/// <summary>API'nin yanıtı başarısız (4xx/5xx) olduğunda fırlatılır; ileti kullanıcıya gösterilebilir.</summary>
public class ApiException(HttpStatusCode status, string message, IReadOnlyDictionary<string, string[]>? errors = null) : Exception(message)
{
    public HttpStatusCode Status { get; } = status;
    public IReadOnlyDictionary<string, string[]> Errors { get; } = errors ?? new Dictionary<string, string[]>();
}

/// <summary>Token geçersiz/süresi dolmuş: kullanıcı yeniden giriş yapmalı.</summary>
public sealed class ApiUnauthorizedException() : ApiException(HttpStatusCode.Unauthorized, "Oturum süresi doldu.");

/// <summary>Kullanıcının rolü bu işlem için yetersiz.</summary>
public sealed class ApiForbiddenException() : ApiException(HttpStatusCode.Forbidden, "Bu işlem için yetkiniz yok.");

/// <summary>
/// AssetPulse API istemcisi. Oturum açık kullanıcının JWT'sini (çerezde saklı) her isteğe ekler.
/// Arayüz veritabanına doğrudan erişmez; yalnızca bu istemci üzerinden API'yi kullanır.
/// </summary>
public class ApiClient(HttpClient http, IHttpContextAccessor accessor)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Geçersiz kimlik bilgisinde null döner; diğer hatalarda <see cref="ApiException"/> fırlatır.</summary>
    public virtual async Task<LoginResult?> LoginAsync(string username, string password, CancellationToken ct)
    {
        using var res = await http.PostAsJsonAsync("api/auth/login", new { username, password }, Json, ct);
        if (res.StatusCode == HttpStatusCode.Unauthorized) return null;
        if (res.StatusCode == HttpStatusCode.TooManyRequests)
            throw new ApiException(res.StatusCode, "Çok fazla deneme yapıldı; bir dakika sonra tekrar deneyin.");
        return await ReadAsync<LoginResult>(res, ct);
    }

    public virtual Task<Dashboard> DashboardAsync(CancellationToken ct) => GetAsync<Dashboard>("api/dashboard", ct);

    public virtual Task<List<AssetSummary>> AssetsAsync(CancellationToken ct) => GetAsync<List<AssetSummary>>("api/assets", ct);

    public virtual Task<AssetDetail> AssetAsync(int id, CancellationToken ct) => GetAsync<AssetDetail>($"api/assets/{id}", ct);

    public virtual Task<SeriesResult> SeriesAsync(int assetId, string sensorCode, DateTime from, DateTime to, string bucket, CancellationToken ct) =>
        GetAsync<SeriesResult>(
            $"api/assets/{assetId}/sensors/{Uri.EscapeDataString(sensorCode)}/series" +
            $"?from={Uri.EscapeDataString(from.ToString("o"))}&to={Uri.EscapeDataString(to.ToString("o"))}&bucket={bucket}", ct);

    public virtual Task<AlertPage> AlertsAsync(AlertFilter filter, int page, int pageSize, CancellationToken ct) =>
        GetAsync<AlertPage>($"api/alerts?{Query(filter)}&page={page}&pageSize={pageSize}", ct);

    public virtual async Task<(byte[] Content, string FileName)> ExportAlertsAsync(AlertFilter filter, CancellationToken ct)
    {
        using var res = await SendAsync(HttpMethod.Get, $"api/alerts/export.csv?{Query(filter)}", null, ct);
        await EnsureAsync(res, ct);
        return (await res.Content.ReadAsByteArrayAsync(ct), "alarmlar.csv");
    }

    public virtual async Task AcknowledgeAsync(long alertId, string? note, CancellationToken ct)
    {
        using var res = await SendAsync(HttpMethod.Post, $"api/alerts/{alertId}/ack", JsonContent.Create(new { note }, options: Json), ct);
        await EnsureAsync(res, ct);
    }

    public virtual async Task<CreatedAsset> CreateAssetAsync(string code, string name, string category, string? location, CancellationToken ct)
    {
        using var res = await SendAsync(HttpMethod.Post, "api/assets", JsonContent.Create(new { code, name, category, location }, options: Json), ct);
        return await ReadAsync<CreatedAsset>(res, ct);
    }

    public virtual async Task AddSensorAsync(int assetId, string code, string name, string unit, CancellationToken ct)
    {
        using var res = await SendAsync(HttpMethod.Post, $"api/assets/{assetId}/sensors", JsonContent.Create(new { code, name, unit }, options: Json), ct);
        await EnsureAsync(res, ct);
    }

    public virtual async Task AddRuleAsync(int sensorId, string kind, decimal threshold, decimal hysteresis, int consecutiveCount, string severity, CancellationToken ct)
    {
        using var res = await SendAsync(HttpMethod.Post, $"api/sensors/{sensorId}/rules",
            JsonContent.Create(new { kind, threshold, hysteresis, consecutiveCount, severity }, options: Json), ct);
        await EnsureAsync(res, ct);
    }

    public virtual async Task<string> RotateKeyAsync(int assetId, CancellationToken ct)
    {
        using var res = await SendAsync(HttpMethod.Post, $"api/assets/{assetId}/rotate-key", null, ct);
        return (await ReadAsync<RotatedKey>(res, ct)).ApiKey;
    }

    // ---- altyapı ----

    private static string Query(AlertFilter f)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(f.Status)) parts.Add($"status={Uri.EscapeDataString(f.Status)}");
        else if (f.OnlyOpen) parts.Add("open=true");
        if (!string.IsNullOrEmpty(f.Severity)) parts.Add($"severity={Uri.EscapeDataString(f.Severity)}");
        if (f.AssetId is { } id) parts.Add($"assetId={id}");
        return string.Join('&', parts);
    }

    private async Task<T> GetAsync<T>(string url, CancellationToken ct)
    {
        using var res = await SendAsync(HttpMethod.Get, url, null, ct);
        return await ReadAsync<T>(res, ct);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, HttpContent? content, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, url) { Content = content };
        var context = accessor.HttpContext;
        var token = context is null ? null : await context.GetTokenAsync("access_token");
        if (!string.IsNullOrEmpty(token)) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await http.SendAsync(req, ct);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage res, CancellationToken ct)
    {
        await EnsureAsync(res, ct);
        return await res.Content.ReadFromJsonAsync<T>(Json, ct)
            ?? throw new ApiException(res.StatusCode, "API boş yanıt döndürdü.");
    }

    private static async Task EnsureAsync(HttpResponseMessage res, CancellationToken ct)
    {
        if (res.IsSuccessStatusCode) return;
        if (res.StatusCode == HttpStatusCode.Unauthorized) throw new ApiUnauthorizedException();
        if (res.StatusCode == HttpStatusCode.Forbidden) throw new ApiForbiddenException();

        // API hataları RFC 9457 "problem details" biçimindedir (title/detail ve doğrulamada errors).
        string message = $"API hatası ({(int)res.StatusCode}).";
        Dictionary<string, string[]>? errors = null;
        try
        {
            using var doc = await JsonDocument.ParseAsync(await res.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            var root = doc.RootElement;
            if (root.TryGetProperty("errors", out var e) && e.ValueKind == JsonValueKind.Object)
            {
                errors = e.EnumerateObject().ToDictionary(
                    p => p.Name, p => p.Value.EnumerateArray().Select(x => x.GetString() ?? "").ToArray());
                message = string.Join(" ", errors.Values.SelectMany(v => v));
            }
            else if (root.TryGetProperty("detail", out var d) && d.ValueKind == JsonValueKind.String) message = d.GetString()!;
            else if (root.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String) message = t.GetString()!;
        }
        catch (JsonException) { /* gövde JSON değil: varsayılan ileti kullanılır */ }
        throw new ApiException(res.StatusCode, message, errors);
    }
}
