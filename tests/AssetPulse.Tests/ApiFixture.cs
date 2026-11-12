using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Xunit;

namespace AssetPulse.Tests;

/// <summary>
/// Gerçek SQL Server'a karşı çalışan entegrasyon testleri için ortak ortam.
/// ASSETPULSE_TEST_SQL (ör. "Server=localhost,1433;User Id=sa;Password=...;TrustServerCertificate=True")
/// tanımlı değilse bu testler atlanır. Her çalıştırma kendi geçici veritabanını oluşturur ve sonunda siler.
/// </summary>
public sealed class ApiFixture : IAsyncLifetime
{
    public const string AdminPassword = "Test-Admin-Parola-1";
    public const string OperatorPassword = "Test-Operator-Parola-1";

    public static readonly string? ServerConnection = Environment.GetEnvironmentVariable("ASSETPULSE_TEST_SQL");
    public static bool Available => !string.IsNullOrWhiteSpace(ServerConnection);

    public static readonly StringWriter ServerLog = new();

    public static string LogTail()
    {
        var text = ServerLog.ToString();
        return text.Length <= 3000 ? text : text[^3000..];
    }

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private readonly string _database = "AssetPulse_T_" + Guid.NewGuid().ToString("N")[..10];
    private WebApplicationFactory<Program>? _factory;

    public HttpClient Anonymous { get; private set; } = default!;
    public HttpClient Admin { get; private set; } = default!;
    public HttpClient Operator { get; private set; } = default!;

    public async Task InitializeAsync()
    {
        if (!Available) return;

        var cs = new SqlConnectionStringBuilder(ServerConnection) { InitialCatalog = _database }.ConnectionString;
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", cs);
        Environment.SetEnvironmentVariable("Jwt__Secret", "test-only-secret-test-only-secret-1234567890");
        Environment.SetEnvironmentVariable("Seed__AdminPassword", AdminPassword);
        Environment.SetEnvironmentVariable("Seed__OperatorPassword", OperatorPassword);
        Environment.SetEnvironmentVariable("Seed__DemoData", "false");

        // Sunucu günlükleri bellekte toplanır; beklenmeyen bir yanıtta hata mesajına eklenir.
        Console.SetOut(TextWriter.Synchronized(ServerLog));
        _factory = new WebApplicationFactory<Program>();
        Anonymous = _factory.CreateClient();
        Admin = await LoginAsync("admin", AdminPassword);
        Operator = await LoginAsync("operator", OperatorPassword);
    }

    private async Task<HttpClient> LoginAsync(string user, string password)
    {
        var client = _factory!.CreateClient();
        var res = await client.PostAsJsonAsync("/api/auth/login", new { username = user, password });
        res.EnsureSuccessStatusCode();
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", body.GetProperty("token").GetString());
        return client;
    }

    public HttpClient Device(string apiKey)
    {
        var client = _factory!.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
        return client;
    }

    public async Task DisposeAsync()
    {
        _factory?.Dispose();
        if (!Available) return;
        SqlConnection.ClearAllPools();
        await using var conn = new SqlConnection(ServerConnection);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"IF DB_ID(N'{_database}') IS NOT NULL BEGIN ALTER DATABASE [{_database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_database}]; END";
        await cmd.ExecuteNonQueryAsync();
    }
}

/// <summary>SQL Server yoksa testi atlar.</summary>
public sealed class SqlFactAttribute : FactAttribute
{
    public SqlFactAttribute()
    {
        if (!ApiFixture.Available) Skip = "ASSETPULSE_TEST_SQL tanımlı değil; SQL Server gerektiren test atlandı.";
    }
}
