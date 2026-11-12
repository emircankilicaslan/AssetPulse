using AssetPulse.Api.Data;
using AssetPulse.Api.Domain;
using AssetPulse.Api.Security;

namespace AssetPulse.Api.Services;

/// <summary>Başlangıçta kullanıcıları oluşturur; isteğe bağlı olarak sentetik demo verisi üretir.</summary>
public static class Seeder
{
    public static async Task RunAsync(IServiceProvider services, IConfiguration config, CancellationToken ct = default)
    {
        using var scope = services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserStore>();

        var adminPassword = config["Seed:AdminPassword"];
        var operatorPassword = config["Seed:OperatorPassword"];
        if (!string.IsNullOrEmpty(adminPassword)) await users.EnsureAsync("admin", Check(adminPassword), "Admin", ct);
        if (!string.IsNullOrEmpty(operatorPassword)) await users.EnsureAsync("operator", Check(operatorPassword), "Operator", ct);

        if (config.GetValue("Seed:DemoData", false))
            await DemoData.SeedAsync(scope.ServiceProvider, ct);
    }

    private static string Check(string password) =>
        password.Length >= 10 ? password : throw new InvalidOperationException("Seed parolaları en az 10 karakter olmalıdır.");
}

/// <summary>
/// Demo için 48 saatlik, deterministik sentetik ölçümler. Gerçek bir sahadan gelmez; panelin ve alarm akışının
/// dolu görünmesi için üretilir (aynı tohum aynı veriyi verir).
/// </summary>
public static class DemoData
{
    private sealed record SensorPlan(string Code, string Name, string Unit, double Base, double Amplitude, double Noise,
        RuleKind Kind, decimal Threshold, decimal Hysteresis, int Consecutive, Severity Severity,
        // Anomali: (şu andan kaç dakika önce başlar, süresi dakika, eklenen/değişen değer)
        (int StartMinutesAgo, int Minutes, double Delta)? Anomaly = null, int? SilentForMinutes = null);

    private sealed record AssetPlan(string Code, string Name, AssetCategory Category, string Location, SensorPlan[] Sensors);

    private static readonly AssetPlan[] Plans =
    [
        new("TM-ANK-01", "Ankara TM-1 Güç Transformatörü", AssetCategory.Transformer, "Ankara / Temelli",
        [
            new("oil-temp", "Yağ sıcaklığı", "°C", 58, 9, 1.2, RuleKind.UpperLimit, 85, 3, 3, Severity.Critical, (45, 45, 38)),
            new("load", "Yük oranı", "%", 62, 18, 2.5, RuleKind.UpperLimit, 95, 5, 2, Severity.Warning),
        ]),
        new("TM-IST-03", "İstanbul TM-3 Güç Transformatörü", AssetCategory.Transformer, "İstanbul / Hadımköy",
        [
            new("oil-temp", "Yağ sıcaklığı", "°C", 54, 7, 1.0, RuleKind.UpperLimit, 85, 3, 3, Severity.Critical),
        ]),
        new("LGT-RWY-03L", "Pist 03L Eksen Işık Regülatörü", AssetCategory.Lighting, "Esenboğa / Pist 03L",
        [
            new("current", "Devre akımı", "A", 6.60, 0.05, 0.03, RuleKind.LowerLimit, 6.20m, 0.15m, 3, Severity.Warning, (1500, 30, -0.7)),
        ]),
        new("SIG-HAT-112", "Hat Km 112 Sinyal Panosu", AssetCategory.Signalling, "Ankara–Eskişehir hattı",
        [
            new("battery-v", "Akü gerilimi", "V", 24.5, 0.25, 0.08, RuleKind.LowerLimit, 22m, 0.5m, 2, Severity.Critical, SilentForMinutes: 190),
        ]),
        new("PMP-TRF-07", "Terfi Pompa İstasyonu 7", AssetCategory.Pump, "Polatlı",
        [
            new("vibration", "Titreşim", "mm/s", 2.4, 0.4, 0.15, RuleKind.UpperLimit, 4.5m, 0.4m, 3, Severity.Warning, (130, 130, 2.6)),
        ]),
        new("GEN-YDK-02", "Yedek Jeneratör 2", AssetCategory.Generator, "Gölbaşı",
        [
            new("coolant-temp", "Soğutma suyu", "°C", 72, 3, 0.8, RuleKind.UpperLimit, 98, 3, 3, Severity.Critical),
            new("fuel-level", "Yakıt seviyesi", "%", 78, 1.5, 0.3, RuleKind.LowerLimit, 20, 3, 2, Severity.Warning),
        ]),
    ];

    public static async Task SeedAsync(IServiceProvider sp, CancellationToken ct)
    {
        var assets = sp.GetRequiredService<AssetStore>();
        if (await assets.AnyAsync(ct)) return; // yalnızca boş veritabanında

        var ingestion = sp.GetRequiredService<IngestionService>();
        var alerts = sp.GetRequiredService<AlertStore>();
        var clock = sp.GetRequiredService<TimeProvider>();
        var now = clock.GetUtcNow().UtcDateTime;
        var start = now.AddHours(-48);
        var rng = new Random(42);

        foreach (var plan in Plans)
        {
            var (_, hash) = ApiKeys.Generate(); // demo cihazlarının anahtarı kullanılmaz; yenilenebilir
            var assetId = await assets.CreateAsync(plan.Code, plan.Name, plan.Category, plan.Location, hash, ct);
            var inputs = new List<ReadingInput>();

            foreach (var s in plan.Sensors)
            {
                var sensorId = await assets.AddSensorAsync(assetId, s.Code, s.Name, s.Unit, ct);
                await assets.AddRuleAsync(sensorId, s.Kind, s.Threshold, s.Hysteresis, s.Consecutive, s.Severity, ct);

                var end = s.SilentForMinutes is { } silent ? now.AddMinutes(-silent) : now;
                for (var t = start; t <= end; t = t.AddMinutes(5))
                {
                    var hourOfDay = t.Hour + t.Minute / 60.0;
                    var value = s.Base + s.Amplitude * Math.Sin((hourOfDay - 9) / 24 * 2 * Math.PI) + (rng.NextDouble() - 0.5) * 2 * s.Noise;
                    if (s.Anomaly is { } a)
                    {
                        var minutesAgo = (now - t).TotalMinutes;
                        if (minutesAgo <= a.StartMinutesAgo && minutesAgo >= a.StartMinutesAgo - a.Minutes) value += a.Delta;
                    }
                    inputs.Add(new ReadingInput(s.Code, t, Math.Round((decimal)value, 2)));
                }
            }

            foreach (var chunk in inputs.OrderBy(i => i.MeasuredAt).Chunk(IngestionService.MaxBatch))
                await ingestion.IngestAsync(assetId, chunk, ct);
        }

        // Pompa titreşim alarmı bir operatör tarafından üstlenilmiş görünsün.
        var open = await alerts.ListAsync(new AlertFilter(AlertStatus.Active, false, null, null), 1, 100, ct);
        foreach (var alert in open.Items.Where(a => a.AssetCode == "PMP-TRF-07"))
            await alerts.AcknowledgeAsync(alert.Id, "operator", "Bakım ekibi yönlendirildi.", now, ct);
    }
}
