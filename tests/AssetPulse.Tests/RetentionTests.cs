using AssetPulse.Api.Data;
using AssetPulse.Api.Endpoints;
using AssetPulse.Api.Services;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AssetPulse.Tests;

/// <summary>Ölçüm saklama süresi: eski ölçümler silinir, her sensörün son ölçümü ve yeni ölçümler korunur.</summary>
public sealed class RetentionTests : IAsyncLifetime
{
    private static readonly DateTime Simdi = new(2026, 11, 13, 12, 0, 0, DateTimeKind.Utc);

    private readonly string _database = "AssetPulse_R_" + Guid.NewGuid().ToString("N")[..10];
    private string ConnectionString => new SqlConnectionStringBuilder(ApiFixture.ServerConnection) { InitialCatalog = _database }.ConnectionString;

    private Db _db = default!;
    private ReadingRetentionStore _store = default!;

    public Task InitializeAsync()
    {
        if (!ApiFixture.Available) return Task.CompletedTask;
        Migrator.Run(ConnectionString, NullLogger.Instance);
        _db = new Db(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Default"] = ConnectionString }).Build());
        _store = new ReadingRetentionStore(_db);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (!ApiFixture.Available) return;
        SqlConnection.ClearAllPools();
        await using var conn = new SqlConnection(ApiFixture.ServerConnection);
        await conn.OpenAsync();
        await conn.ExecuteAsync($"IF DB_ID(N'{_database}') IS NOT NULL BEGIN ALTER DATABASE [{_database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_database}]; END");
    }

    private async Task<int> SensorEkle(string kod)
    {
        await using var conn = await _db.OpenAsync();
        var assetId = await conn.ExecuteScalarAsync<int>(
            "INSERT dbo.Assets (Code, Name, Category, ApiKeyHash) OUTPUT INSERTED.Id VALUES (@kod, @kod, 1, @hash)",
            new { kod, hash = new string('a', 60) + kod.PadLeft(4, '0')[..4] });
        return await conn.ExecuteScalarAsync<int>(
            "INSERT dbo.Sensors (AssetId, Code, Name, Unit) OUTPUT INSERTED.Id VALUES (@assetId, 'temp', 'Sıcaklık', 'C')", new { assetId });
    }

    private async Task Olc(int sensorId, params int[] gunOnce)
    {
        await using var conn = await _db.OpenAsync();
        foreach (var gun in gunOnce)
            await conn.ExecuteAsync("INSERT dbo.Readings (SensorId, MeasuredAt, Value) VALUES (@sensorId, @at, 1)", new { sensorId, at = Simdi.AddDays(-gun) });
    }

    private async Task<List<int>> KalanGunler(int sensorId)
    {
        await using var conn = await _db.OpenAsync();
        var zamanlar = await conn.QueryAsync<DateTime>("SELECT MeasuredAt FROM dbo.Readings WHERE SensorId = @sensorId ORDER BY MeasuredAt DESC", new { sensorId });
        return zamanlar.Select(z => (int)Math.Round((Simdi - z).TotalDays)).ToList();
    }

    [SqlFact]
    public async Task EskiOlcumleriSiler_YenileriKorur()
    {
        var sensor = await SensorEkle("A");
        await Olc(sensor, 10, 5, 1, 0);

        var silinen = await _store.PurgeAsync(Simdi.AddDays(-7), 1000, CancellationToken.None);

        Assert.Equal(1, silinen);
        Assert.Equal([0, 1, 5], await KalanGunler(sensor));
    }

    [SqlFact]
    public async Task SensorunSonOlcumu_YasiNeOlursaOlsunKorunur()
    {
        var yalnizEski = await SensorEkle("B");
        var cokEski = await SensorEkle("C");
        await Olc(yalnizEski, 30);
        await Olc(cokEski, 40, 35);

        var silinen = await _store.PurgeAsync(Simdi.AddDays(-7), 1000, CancellationToken.None);

        Assert.Equal(1, silinen);                          // yalnızca 40 günlük
        Assert.Equal([30], await KalanGunler(yalnizEski)); // tek ve son ölçüm kalır
        Assert.Equal([35], await KalanGunler(cokEski));    // son olan kalır
    }

    [SqlFact]
    public async Task BirdenFazlaParcadaSiler()
    {
        var sensor = await SensorEkle("D");
        await Olc(sensor, 50, 49, 48, 47, 46, 45, 44, 0);

        var silinen = await _store.PurgeAsync(Simdi.AddDays(-7), batchSize: 3, CancellationToken.None);

        Assert.Equal(7, silinen); // 3 + 3 + 1
        Assert.Equal([0], await KalanGunler(sensor));
    }

    [SqlFact]
    public async Task SilinecekKayitYoksa_SifirDoner()
    {
        var sensor = await SensorEkle("E");
        await Olc(sensor, 2, 1, 0);

        Assert.Equal(0, await _store.PurgeAsync(Simdi.AddDays(-7), 1000, CancellationToken.None));
        Assert.Equal([0, 1, 2], await KalanGunler(sensor));
    }

    [Fact]
    public async Task GecersizParcaBoyutuReddedilir()
    {
        var store = new ReadingRetentionStore(new Db(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Default"] = "Server=yok" }).Build()));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.PurgeAsync(Simdi, 0, CancellationToken.None));
    }

    [Fact]
    public async Task VarsayilanAyarda_Kapali_HicbirSeySilinmez()
    {
        Assert.Equal(0, new PulseOptions().RetentionDays);

        // Veritabanına hiç bağlanmaması gerekir: bağlanılamayan bir adres verilir, servis hata vermeden tamamlanmalıdır.
        var store = new ReadingRetentionStore(new Db(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Default"] = "Server=yok;Connect Timeout=1" }).Build()));
        var servis = new ReadingRetentionService(store, Options.Create(new PulseOptions()), TimeProvider.System, NullLogger<ReadingRetentionService>.Instance);

        await servis.StartAsync(CancellationToken.None);
        await servis.ExecuteTask!;
        await servis.StopAsync(CancellationToken.None);
    }
}
