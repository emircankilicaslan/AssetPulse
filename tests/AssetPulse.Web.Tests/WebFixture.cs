using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AssetPulse.Web.Api;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace AssetPulse.Web.Tests;

/// <summary>API'nin yerine geçen sahte sunucu: yönlendirmeleri ve gelen istekleri kaydeder.</summary>
public sealed class FakeApiHandler : HttpMessageHandler
{
    public sealed record Call(HttpMethod Method, string Path, string Query, string? Authorization, string? Body);

    public List<Call> Calls { get; } = [];
    public Dictionary<string, Func<HttpResponseMessage>> Routes { get; } = new(StringComparer.OrdinalIgnoreCase);

    public void Json(string route, object body, HttpStatusCode status = HttpStatusCode.OK) =>
        Routes[route] = () => new HttpResponseMessage(status)
        {
            Content = new StringContent(JsonSerializer.Serialize(body, new JsonSerializerOptions(JsonSerializerDefaults.Web)), Encoding.UTF8, "application/json"),
        };

    public void Status(string route, HttpStatusCode status) => Routes[route] = () => new HttpResponseMessage(status);

    public void Throw(string route) => Routes[route] = () => throw new HttpRequestException("bağlantı reddedildi");

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath.TrimStart('/');
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        lock (Calls) Calls.Add(new Call(request.Method, path, request.RequestUri.Query, request.Headers.Authorization?.ToString(), body));
        return Routes.TryGetValue($"{request.Method} {path}", out var factory) ? factory() : new HttpResponseMessage(HttpStatusCode.NotFound);
    }
}

public sealed class WebFixture : WebApplicationFactory<Program>
{
    public FakeApiHandler Api { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder) =>
        builder.ConfigureTestServices(services =>
            services.AddHttpClient<ApiClient>().ConfigurePrimaryHttpMessageHandler(() => Api));

    public HttpClient NewClient() => CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
}

public static partial class Html
{
    [GeneratedRegex("name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"")]
    private static partial Regex TokenPattern();

    public static string Token(string html) =>
        TokenPattern().Match(html) is { Success: true } m ? m.Groups[1].Value : throw new InvalidOperationException("Sayfada antiforgery belirteci yok.");

    public static async Task<string> BodyAsync(HttpResponseMessage res) => WebUtility.HtmlDecode(await res.Content.ReadAsStringAsync());
}

/// <summary>Testlerde kullanılan örnek API yanıtları.</summary>
public static class Sample
{
    public static object Login(string role = "Operator", string user = "operator") =>
        new { token = "jwt-" + user, expiresAt = DateTime.UtcNow.AddHours(8), username = user, role };

    public static object Alert(long id = 5, string status = "Active") => new
    {
        id, assetId = 1, assetCode = "TM-ANK-01", assetName = "Ankara TM-1 Güç Transformatörü", sensorId = 1, sensorCode = "oil-temp",
        unit = "°C", kind = "UpperLimit", threshold = 85, severity = "Critical", status,
        openedAt = DateTime.UtcNow.AddMinutes(-30), openValue = 96.4, acknowledgedBy = (string?)null,
        acknowledgedAt = (DateTime?)null, clearedAt = (DateTime?)null, note = (string?)null,
    };

    public static object Asset(string health = "Critical") => new
    {
        id = 1, code = "TM-ANK-01", name = "Ankara TM-1 Güç Transformatörü", category = "Transformer", location = "Ankara / Temelli",
        health, openAlerts = 1, lastReadingAt = DateTime.UtcNow.AddMinutes(-2),
    };

    public static object Dashboard() => new
    {
        assets = new { total = 6, byHealth = new Dictionary<string, int> { ["Normal"] = 3, ["Warning"] = 1, ["Critical"] = 1, ["NoData"] = 1 } },
        openAlerts = new Dictionary<string, int> { ["Info"] = 0, ["Warning"] = 1, ["Critical"] = 1 },
        latestOpenAlerts = new[] { Alert() },
        attention = new[] { Asset() },
    };
}
