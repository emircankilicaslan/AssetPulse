using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace AssetPulse.Tests;

public class ApiTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    private static string NewCode(string prefix) => $"{prefix}-{Guid.NewGuid().ToString("N")[..8]}";

    private static string Iso(DateTime utc) => utc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

    private async Task<(int AssetId, string Key, int SensorId)> CreateAssetWithRuleAsync(
        string sensor = "temp", string kind = "UpperLimit", decimal threshold = 80, int consecutive = 2, decimal hysteresis = 0)
    {
        var res = await api.Admin.PostAsJsonAsync("/api/assets", new { code = NewCode("AST"), name = "Test Trafosu", category = "Transformer", location = "Test" });
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var asset = await res.Content.ReadFromJsonAsync<JsonElement>();
        var id = asset.GetProperty("id").GetInt32();
        var key = asset.GetProperty("apiKey").GetString()!;

        var s = await api.Admin.PostAsJsonAsync($"/api/assets/{id}/sensors", new { code = sensor, name = "Sıcaklık", unit = "°C" });
        Assert.Equal(HttpStatusCode.Created, s.StatusCode);
        var sensorId = (await s.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();

        var r = await api.Admin.PostAsJsonAsync($"/api/sensors/{sensorId}/rules",
            new { kind, threshold, hysteresis, consecutiveCount = consecutive, severity = "Critical" });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return (id, key, sensorId);
    }

    private static object Reading(string sensor, DateTime at, decimal value) => new { sensor, measuredAt = Iso(at), value };

    /// <summary>Başarılı yanıtın JSON gövdesini okur; başarısızsa durum kodu ve gövde hata mesajına girer.</summary>
    private static async Task<JsonElement> Body(HttpResponseMessage res)
    {
        if (!res.IsSuccessStatusCode)
            throw new Xunit.Sdk.XunitException($"Beklenmeyen yanıt {(int)res.StatusCode}: {await res.Content.ReadAsStringAsync()}\n--- sunucu günlüğü ---\n{ApiFixture.LogTail()}");
        return await res.Content.ReadFromJsonAsync<JsonElement>();
    }

    [SqlFact]
    public async Task Kimliksiz_istek_reddedilir_saglik_ucu_aciktir()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.Anonymous.GetAsync("/api/assets")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await api.Anonymous.GetAsync("/health")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await api.Anonymous.GetAsync("/health/ready")).StatusCode);
    }

    [SqlFact]
    public async Task Yanlis_parola_401_doner()
    {
        var res = await api.Anonymous.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = "yanlis-parola-123" });
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [SqlFact]
    public async Task Operator_varlik_olusturamaz()
    {
        var res = await api.Operator.PostAsJsonAsync("/api/assets", new { code = NewCode("OP"), name = "x", category = "Pump" });
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [SqlFact]
    public async Task Ayni_kodlu_varlik_409_doner_gecersiz_girdi_400()
    {
        var code = NewCode("DUP");
        Assert.Equal(HttpStatusCode.Created, (await api.Admin.PostAsJsonAsync("/api/assets", new { code, name = "A", category = "Pump" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await api.Admin.PostAsJsonAsync("/api/assets", new { code, name = "B", category = "Pump" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await api.Admin.PostAsJsonAsync("/api/assets", new { code = "!", name = "B", category = "Pump" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await api.Admin.PostAsJsonAsync("/api/assets", new { code = NewCode("X"), name = "", category = "Pump" })).StatusCode);
    }

    [SqlFact]
    public async Task Gecersiz_api_anahtari_ile_olcum_gonderilemez()
    {
        var res = await api.Device("ap_yanlis").PostAsJsonAsync("/api/readings", new { readings = new[] { Reading("temp", DateTime.UtcNow, 1) } });
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.Anonymous.PostAsJsonAsync("/api/readings", new { readings = new[] { Reading("temp", DateTime.UtcNow, 1) } })).StatusCode);
    }

    [SqlFact]
    public async Task Olcum_alimi_tekrarlari_yok_sayar_ve_gecersizleri_reddeder()
    {
        var (_, key, _) = await CreateAssetWithRuleAsync();
        var device = api.Device(key);
        var t = DateTime.UtcNow.AddMinutes(-10);

        var first = await Body(await device.PostAsJsonAsync("/api/readings", new
        {
            readings = new object[]
            {
                Reading("temp", t, 12.3456m),
                Reading("temp", t.AddMinutes(1), 13),
                Reading("temp", t.AddMinutes(1), 99),          // aynı istekte tekrar
                Reading("yok", t, 1),                          // bilinmeyen sensör
                Reading("temp", DateTime.UtcNow.AddDays(-40), 1), // çok eski
                Reading("temp", DateTime.UtcNow.AddHours(2), 1),  // gelecekte
            },
        }));
        Assert.Equal(6, first.GetProperty("received").GetInt32());
        Assert.Equal(2, first.GetProperty("accepted").GetInt32());
        Assert.Equal(1, first.GetProperty("duplicates").GetInt32());
        Assert.Equal(3, first.GetProperty("rejected").GetInt32());

        // Aynı veriyi yeniden göndermek (ağ yeniden denemesi) hiçbir şey eklemez.
        var again = await Body(await device.PostAsJsonAsync("/api/readings", new { readings = new[] { Reading("temp", t, 12.3456m) } }));
        Assert.Equal(0, again.GetProperty("accepted").GetInt32());
        Assert.Equal(1, again.GetProperty("duplicates").GetInt32());
    }

    [SqlFact]
    public async Task Bos_ve_asiri_buyuk_istek_400_doner()
    {
        var (_, key, _) = await CreateAssetWithRuleAsync();
        var device = api.Device(key);
        Assert.Equal(HttpStatusCode.BadRequest, (await device.PostAsJsonAsync("/api/readings", new { readings = Array.Empty<object>() })).StatusCode);
        var tooMany = Enumerable.Range(0, 1001).Select(i => Reading("temp", DateTime.UtcNow.AddSeconds(-i), 1)).ToArray();
        Assert.Equal(HttpStatusCode.BadRequest, (await device.PostAsJsonAsync("/api/readings", new { readings = tooMany })).StatusCode);
    }

    [SqlFact]
    public async Task Alarm_yasam_dongusu_acilir_uestlenilir_kapanir()
    {
        var (assetId, key, _) = await CreateAssetWithRuleAsync(threshold: 80, consecutive: 2, hysteresis: 2);
        var device = api.Device(key);
        var t = DateTime.UtcNow.AddMinutes(-30);

        // Tek ihlal: henüz alarm yok.
        var r1 = await Body(await device.PostAsJsonAsync("/api/readings", new { readings = new[] { Reading("temp", t, 70), Reading("temp", t.AddMinutes(1), 85) } }));
        Assert.Equal(0, r1.GetProperty("alertsOpened").GetInt32());

        // İkinci ardışık ihlal (ayrı istek): sayaç veritabanında taşınır, alarm açılır.
        var r2 = await Body(await device.PostAsJsonAsync("/api/readings", new { readings = new[] { Reading("temp", t.AddMinutes(2), 86) } }));
        Assert.Equal(1, r2.GetProperty("alertsOpened").GetInt32());

        var list = await Body(await api.Operator.GetAsync($"/api/alerts?assetId={assetId}&open=true"));
        Assert.Equal(1, list.GetProperty("total").GetInt32());
        var alert = list.GetProperty("items")[0];
        Assert.Equal("Active", alert.GetProperty("status").GetString());
        Assert.Equal("Critical", alert.GetProperty("severity").GetString());
        var alertId = alert.GetProperty("id").GetInt64();

        // Varlık Kritik görünür.
        var assets = await Body(await api.Operator.GetAsync("/api/assets?health=Critical"));
        Assert.Contains(assets.EnumerateArray(), a => a.GetProperty("id").GetInt32() == assetId);

        // Üstlenme: ilk istek başarılı, ikincisi çakışma.
        var ack = await api.Operator.PostAsJsonAsync($"/api/alerts/{alertId}/ack", new { note = "Ekip yönlendirildi" });
        Assert.Equal(HttpStatusCode.OK, ack.StatusCode);
        var acked = await Body(ack);
        Assert.Equal("Acknowledged", acked.GetProperty("status").GetString());
        Assert.Equal("operator", acked.GetProperty("acknowledgedBy").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await api.Operator.PostAsJsonAsync($"/api/alerts/{alertId}/ack", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await api.Operator.PostAsJsonAsync("/api/alerts/999999999/ack", new { })).StatusCode);

        // 79 eşiğin altında ama histerezis bandında (kapanış için ≤ 78 gerekir): alarm hâlâ açık.
        await device.PostAsJsonAsync("/api/readings", new { readings = new[] { Reading("temp", t.AddMinutes(3), 79) } });
        var still = await Body(await api.Operator.GetAsync($"/api/alerts/{alertId}"));
        Assert.Equal("Acknowledged", still.GetProperty("status").GetString());

        // Güvenli bölgeye dönüş: kapanır.
        var r3 = await Body(await device.PostAsJsonAsync("/api/readings", new { readings = new[] { Reading("temp", t.AddMinutes(4), 70) } }));
        Assert.Equal(1, r3.GetProperty("alertsCleared").GetInt32());
        var closed = await Body(await api.Operator.GetAsync($"/api/alerts/{alertId}"));
        Assert.Equal("Cleared", closed.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.String, closed.GetProperty("clearedAt").ValueKind);
    }

    [SqlFact]
    public async Task Pasife_alinan_varlik_olcum_kabul_etmez_filodan_cikar_ve_yeniden_etkinlesir()
    {
        var (assetId, key, _) = await CreateAssetWithRuleAsync(threshold: 1000);
        var payload = new { readings = new[] { Reading("temp", DateTime.UtcNow.AddMinutes(-1), 1) } };
        Assert.Equal(HttpStatusCode.OK, (await api.Device(key).PostAsJsonAsync("/api/readings", payload)).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await api.Admin.PostAsync($"/api/assets/{assetId}/deactivate", null)).StatusCode);
        // İdempotent: zaten pasif olanı tekrar pasife almak hata değil.
        Assert.Equal(HttpStatusCode.NoContent, (await api.Admin.PostAsync($"/api/assets/{assetId}/deactivate", null)).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await api.Device(key).PostAsJsonAsync("/api/readings", payload)).StatusCode);
        var fleet = await Body(await api.Operator.GetAsync("/api/assets"));
        Assert.DoesNotContain(fleet.EnumerateArray(), a => a.GetProperty("id").GetInt32() == assetId);
        // Geçmiş korunur: detay hala okunabilir ve pasif görünür.
        var detail = await Body(await api.Operator.GetAsync($"/api/assets/{assetId}"));
        Assert.False(detail.GetProperty("isActive").GetBoolean());

        Assert.Equal(HttpStatusCode.NoContent, (await api.Admin.PostAsync($"/api/assets/{assetId}/activate", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await api.Device(key).PostAsJsonAsync("/api/readings", payload)).StatusCode);
        var fleetAgain = await Body(await api.Operator.GetAsync("/api/assets"));
        Assert.Contains(fleetAgain.EnumerateArray(), a => a.GetProperty("id").GetInt32() == assetId);
    }

    [SqlFact]
    public async Task Operator_pasife_alamaz_olmayan_varlik_404_doner()
    {
        var (assetId, _, _) = await CreateAssetWithRuleAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await api.Operator.PostAsync($"/api/assets/{assetId}/deactivate", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await api.Operator.PostAsync($"/api/assets/{assetId}/activate", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.Anonymous.PostAsync($"/api/assets/{assetId}/deactivate", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await api.Admin.PostAsync("/api/assets/999999/deactivate", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await api.Admin.PostAsync("/api/assets/999999/activate", null)).StatusCode);
    }

    [SqlFact]
    public async Task Gec_gelen_eski_olcum_alarm_degerlendirmesine_girmez()
    {
        var (assetId, key, _) = await CreateAssetWithRuleAsync(consecutive: 1);
        var device = api.Device(key);
        var t = DateTime.UtcNow.AddMinutes(-20);
        await device.PostAsJsonAsync("/api/readings", new { readings = new[] { Reading("temp", t, 50) } });

        // Sensörün son ölçümünden eski, ihlal eden bir değer sonradan gelir: saklanır ama alarm üretmez.
        var late = await Body(await device.PostAsJsonAsync("/api/readings", new { readings = new[] { Reading("temp", t.AddMinutes(-5), 99) } }));
        Assert.Equal(1, late.GetProperty("accepted").GetInt32());
        Assert.Equal(0, late.GetProperty("alertsOpened").GetInt32());
        Assert.Equal(0, (await Body(await api.Admin.GetAsync($"/api/alerts?assetId={assetId}"))).GetProperty("total").GetInt32());
    }

    [SqlFact]
    public async Task Zaman_serisi_saatlik_ozet_ve_ham_veri_doner()
    {
        var (assetId, key, _) = await CreateAssetWithRuleAsync(threshold: 1000);
        var device = api.Device(key);
        var hour = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, DateTime.UtcNow.Day, DateTime.UtcNow.Hour, 0, 0, DateTimeKind.Utc).AddHours(-2);
        await device.PostAsJsonAsync("/api/readings", new
        {
            readings = new[]
            {
                Reading("temp", hour.AddMinutes(5), 10), Reading("temp", hour.AddMinutes(25), 20), Reading("temp", hour.AddMinutes(45), 30),
                Reading("temp", hour.AddHours(1).AddMinutes(10), 100),
            },
        });

        var from = Uri.EscapeDataString(Iso(hour));
        var to = Uri.EscapeDataString(Iso(hour.AddHours(2)));
        var hourly = await Body(await api.Operator.GetAsync($"/api/assets/{assetId}/sensors/temp/series?from={from}&to={to}&bucket=Hour"));
        var points = hourly.GetProperty("points");
        Assert.Equal(2, points.GetArrayLength());
        Assert.Equal(10m, points[0].GetProperty("min").GetDecimal());
        Assert.Equal(20m, points[0].GetProperty("avg").GetDecimal());
        Assert.Equal(30m, points[0].GetProperty("max").GetDecimal());
        Assert.Equal(3, points[0].GetProperty("count").GetInt32());
        Assert.Equal(100m, points[1].GetProperty("max").GetDecimal());
        Assert.EndsWith("Z", points[0].GetProperty("time").GetString());

        var raw = await Body(await api.Operator.GetAsync($"/api/assets/{assetId}/sensors/temp/series?from={from}&to={to}&bucket=Raw"));
        Assert.Equal(4, raw.GetProperty("points").GetArrayLength());

        Assert.Equal(HttpStatusCode.BadRequest, (await api.Operator.GetAsync($"/api/assets/{assetId}/sensors/temp/series?from={to}&to={from}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await api.Operator.GetAsync($"/api/assets/{assetId}/sensors/yok/series")).StatusCode);
    }

    [SqlFact]
    public async Task Ondalik_deger_kayipsiz_saklanir()
    {
        var (assetId, key, _) = await CreateAssetWithRuleAsync(threshold: 1000);
        await api.Device(key).PostAsJsonAsync("/api/readings", new { readings = new[] { Reading("temp", DateTime.UtcNow.AddMinutes(-1), 12.3456m) } });
        var detail = await Body(await api.Operator.GetAsync($"/api/assets/{assetId}"));
        Assert.Equal(12.3456m, detail.GetProperty("sensors")[0].GetProperty("lastValue").GetDecimal());
    }

    [SqlFact]
    public async Task Milisaniye_hassasiyeti_korunur_ve_tekrar_gonderim_yok_sayilir()
    {
        // Zaman damgası datetime'a (3,33 ms'ye yuvarlayan tür) dönüşürse bu test kırılır.
        var (assetId, key, _) = await CreateAssetWithRuleAsync(threshold: 1000);
        var device = api.Device(key);
        var start = DateTime.UtcNow.AddMinutes(-20);
        start = new DateTime(start.Year, start.Month, start.Day, start.Hour, start.Minute, start.Second, DateTimeKind.Utc);

        for (var ms = 0; ms < 1000; ms += 137)
        {
            var at = start.AddMilliseconds(ms);
            var payload = new { readings = new[] { Reading("temp", at, ms) } };
            Assert.Equal(1, (await Body(await device.PostAsJsonAsync("/api/readings", payload))).GetProperty("accepted").GetInt32());
            Assert.Equal(1, (await Body(await device.PostAsJsonAsync("/api/readings", payload))).GetProperty("duplicates").GetInt32());
        }

        var from = Uri.EscapeDataString(Iso(start));
        var to = Uri.EscapeDataString(Iso(start.AddSeconds(2)));
        var raw = await Body(await api.Operator.GetAsync($"/api/assets/{assetId}/sensors/temp/series?from={from}&to={to}&bucket=Raw"));
        var times = raw.GetProperty("points").EnumerateArray().Select(p => p.GetProperty("time").GetDateTime().Millisecond).ToList();
        Assert.Equal([0, 137, 274, 411, 548, 685, 822, 959], times);
    }

    [SqlFact]
    public async Task Anahtar_yenilenince_eskisi_gecersiz_olur()
    {
        var (assetId, oldKey, _) = await CreateAssetWithRuleAsync();
        var rotated = await Body(await api.Admin.PostAsync($"/api/assets/{assetId}/rotate-key", null));
        var newKey = rotated.GetProperty("apiKey").GetString()!;

        var payload = new { readings = new[] { Reading("temp", DateTime.UtcNow.AddMinutes(-1), 1) } };
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.Device(oldKey).PostAsJsonAsync("/api/readings", payload)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await api.Device(newKey).PostAsJsonAsync("/api/readings", payload)).StatusCode);
    }

    [SqlFact]
    public async Task Panel_ve_csv_disa_aktarim_calisir()
    {
        var (assetId, key, _) = await CreateAssetWithRuleAsync(consecutive: 1);
        await api.Device(key).PostAsJsonAsync("/api/readings", new { readings = new[] { Reading("temp", DateTime.UtcNow.AddMinutes(-1), 95) } });

        var dash = await Body(await api.Operator.GetAsync("/api/dashboard"));
        Assert.True(dash.GetProperty("openAlerts").GetProperty("Critical").GetInt32() >= 1);
        Assert.Contains(dash.GetProperty("attention").EnumerateArray(), a => a.GetProperty("id").GetInt32() == assetId);

        var csv = await api.Operator.GetAsync($"/api/alerts/export.csv?assetId={assetId}");
        Assert.Equal(HttpStatusCode.OK, csv.StatusCode);
        Assert.Equal("text/csv", csv.Content.Headers.ContentType?.MediaType);
        var text = await csv.Content.ReadAsStringAsync();
        Assert.Contains("Test Trafosu", text);
        Assert.Contains("Critical", text);
    }

    [SqlFact]
    public async Task Sayfalama_sinirlari_dogrulanir()
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await api.Operator.GetAsync("/api/alerts?pageSize=1000")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await api.Operator.GetAsync("/api/alerts?page=0")).StatusCode);
    }
}
