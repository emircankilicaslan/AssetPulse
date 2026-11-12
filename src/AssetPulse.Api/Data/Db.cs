using System.Data;
using Dapper;
using DbUp;
using Microsoft.Data.SqlClient;

namespace AssetPulse.Api.Data;

/// <summary>Bağlantı fabrikası. Tüm zamanlar UTC olarak saklanır ve okunur.</summary>
public sealed class Db
{
    static Db()
    {
        // Dapper varsayılan olarak DateTime parametresini "datetime" gönderir ve 3,33 ms'ye yuvarlar;
        // DATETIME2(3) sütunlarıyla birebir eşleşmek için açıkça datetime2 kullanılır.
        SqlMapper.AddTypeMap(typeof(DateTime), DbType.DateTime2);
        SqlMapper.AddTypeMap(typeof(DateTime?), DbType.DateTime2);
    }

    public Db(IConfiguration configuration) =>
        ConnectionString = configuration.GetConnectionString("Default")
            ?? throw new InvalidOperationException("ConnectionStrings:Default tanımlı değil.");

    public string ConnectionString { get; }

    public async Task<SqlConnection> OpenAsync(CancellationToken ct = default)
    {
        var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync(ct);
        return conn;
    }
}

/// <summary>
/// Veritabanındaki tüm zamanlar UTC'dir; DATETIME2 kind bilgisi taşımadığından JSON'a her zaman "Z" ekiyle yazılır.
/// </summary>
public sealed class UtcDateTimeJsonConverter : System.Text.Json.Serialization.JsonConverter<DateTime>
{
    public override DateTime Read(ref System.Text.Json.Utf8JsonReader reader, Type typeToConvert, System.Text.Json.JsonSerializerOptions options) =>
        Utc(reader.GetDateTime());

    private static DateTime Utc(DateTime value) =>
        value.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(value, DateTimeKind.Utc) : value.ToUniversalTime();

    public override void Write(System.Text.Json.Utf8JsonWriter writer, DateTime value, System.Text.Json.JsonSerializerOptions options) =>
        writer.WriteStringValue(DateTime.SpecifyKind(value, DateTimeKind.Utc));
}

/// <summary>Gömülü SQL betiklerini DbUp ile sırayla uygular (veritabanı yoksa oluşturur).</summary>
public static class Migrator
{
    public static void Run(string connectionString, ILogger logger, int attempts = 30)
    {
        for (var i = 1; ; i++)
        {
            try
            {
                EnsureDatabase.For.SqlDatabase(connectionString);
                var result = DeployChanges.To
                    .SqlDatabase(connectionString)
                    .WithScriptsEmbeddedInAssembly(typeof(Migrator).Assembly)
                    .LogToConsole()
                    .Build()
                    .PerformUpgrade();
                if (!result.Successful) throw result.Error;
                return;
            }
            catch (SqlException ex) when (i < attempts)
            {
                // Konteyner ile birlikte başlarken SQL Server henüz hazır olmayabilir.
                logger.LogWarning("Veritabanına bağlanılamadı ({Attempt}/{Max}): {Message}", i, attempts, ex.Message);
                Thread.Sleep(TimeSpan.FromSeconds(2));
            }
        }
    }
}
