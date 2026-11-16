using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClosedXML.Excel;
using Xunit;

namespace AssetPulse.Tests;

/// <summary>Denetim kaydı (kim, ne zaman, ne yaptı) ve Excel çıktıları; gerçek SQL Server'a karşı.</summary>
public class AuditTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    private static string NewCode(string prefix) => $"{prefix}-{Guid.NewGuid().ToString("N")[..8]}";

    private static async Task<JsonElement> Body(HttpResponseMessage res)
    {
        if (!res.IsSuccessStatusCode)
            throw new Xunit.Sdk.XunitException($"Beklenmeyen yanıt {(int)res.StatusCode}: {await res.Content.ReadAsStringAsync()}\n--- sunucu günlüğü ---\n{ApiFixture.LogTail()}");
        return await res.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<List<JsonElement>> Entries(string query)
    {
        var body = await Body(await api.Admin.GetAsync($"/api/audit?{query}"));
        return body.GetProperty("items").EnumerateArray().ToList();
    }

    [SqlFact]
    public async Task Admin_islemleri_kullanici_ve_zamanla_kaydedilir_anahtar_kayda_girmez()
    {
        var before = DateTime.UtcNow.AddSeconds(-5);
        var code = NewCode("AUD");
        var created = await Body(await api.Admin.PostAsJsonAsync("/api/assets", new { code, name = "Denetim Trafosu", category = "Transformer" }));
        var id = created.GetProperty("id").GetInt32();
        var apiKey = created.GetProperty("apiKey").GetString()!;

        var sensor = await Body(await api.Admin.PostAsJsonAsync($"/api/assets/{id}/sensors", new { code = "temp", name = "Sıcaklık", unit = "°C" }));
        var sensorId = sensor.GetProperty("id").GetInt32();
        await Body(await api.Admin.PostAsJsonAsync($"/api/sensors/{sensorId}/rules", new { kind = "UpperLimit", threshold = 85, severity = "Critical" }));
        var rotated = await Body(await api.Admin.PostAsync($"/api/assets/{id}/rotate-key", null));
        Assert.Equal(HttpStatusCode.NoContent, (await api.Admin.PostAsync($"/api/assets/{id}/deactivate", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await api.Admin.PostAsync($"/api/assets/{id}/activate", null)).StatusCode);

        var raw = await api.Admin.GetStringAsync("/api/audit?pageSize=100");
        var items = JsonDocument.Parse(raw).RootElement.GetProperty("items").EnumerateArray()
            .Where(e => e.GetProperty("entityType").GetString() is "Asset" or "Sensor" or "Rule"
                        && (e.GetProperty("entityId").GetString() == id.ToString()
                            || e.GetProperty("entityId").GetString() == sensorId.ToString()
                            || e.GetProperty("details").GetString()?.Contains(code) == true))
            .Select(e => e.GetProperty("action").GetString()).ToList();

        foreach (var action in new[] { "AssetCreated", "SensorCreated", "RuleCreated", "AssetKeyRotated", "AssetDeactivated", "AssetActivated" })
            Assert.Contains(action, items);

        var first = (await Entries($"action=AssetCreated&pageSize=100")).First(e => e.GetProperty("entityId").GetString() == id.ToString());
        Assert.Equal("admin", first.GetProperty("actor").GetString());
        Assert.Equal("Admin", first.GetProperty("actorRole").GetString());
        Assert.True(first.GetProperty("occurredAt").GetDateTime().ToUniversalTime() >= before);
        Assert.Contains(code, first.GetProperty("details").GetString());

        // Cihaz anahtarları (ne ilk ne yenilenen) hiçbir zaman kayda girmez.
        Assert.DoesNotContain(apiKey, raw);
        Assert.DoesNotContain(rotated.GetProperty("apiKey").GetString()!, raw);
    }

    [SqlFact]
    public async Task Basarisiz_giris_denenen_adla_kaydedilir_parola_kayda_girmez()
    {
        var user = NewCode("kimlik");
        const string password = "cok-gizli-yanlis-parola-9";
        var res = await api.Anonymous.PostAsJsonAsync("/api/auth/login", new { username = user, password });
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);

        var raw = await api.Admin.GetStringAsync($"/api/audit?actor={user}");
        var items = JsonDocument.Parse(raw).RootElement.GetProperty("items");
        Assert.Equal(1, items.GetArrayLength());
        Assert.Equal("LoginFailed", items[0].GetProperty("action").GetString());
        Assert.DoesNotContain(password, raw);
    }

    [SqlFact]
    public async Task Basarili_giris_kaydedilir()
    {
        var before = (await Entries("actor=operator&action=Login&pageSize=100")).Count;
        await api.Anonymous.PostAsJsonAsync("/api/auth/login", new { username = "operator", password = ApiFixture.OperatorPassword });
        var after = await Entries("actor=operator&action=Login&pageSize=100");
        Assert.Equal(before + 1, after.Count);
        Assert.Equal("Operator", after[0].GetProperty("actorRole").GetString());
    }

    [SqlFact]
    public async Task Alarm_ustlenme_kaydedilir()
    {
        var created = await Body(await api.Admin.PostAsJsonAsync("/api/assets", new { code = NewCode("ACK"), name = "Üstlenme Trafosu", category = "Transformer" }));
        var id = created.GetProperty("id").GetInt32();
        var key = created.GetProperty("apiKey").GetString()!;
        var sensorId = (await Body(await api.Admin.PostAsJsonAsync($"/api/assets/{id}/sensors", new { code = "temp", name = "Sıcaklık", unit = "°C" }))).GetProperty("id").GetInt32();
        await Body(await api.Admin.PostAsJsonAsync($"/api/sensors/{sensorId}/rules", new { kind = "UpperLimit", threshold = 80, consecutiveCount = 1, severity = "Critical" }));

        var device = api.Device(key);
        await Body(await device.PostAsJsonAsync("/api/readings", new { readings = new[] { new { sensor = "temp", measuredAt = DateTime.UtcNow.AddMinutes(-1).ToString("yyyy-MM-ddTHH:mm:ss.fffZ"), value = 95 } } }));
        var alerts = await Body(await api.Operator.GetAsync($"/api/alerts?assetId={id}"));
        var alertId = alerts.GetProperty("items")[0].GetProperty("id").GetInt64();

        await Body(await api.Operator.PostAsJsonAsync($"/api/alerts/{alertId}/ack", new { note = "Ekip yönlendirildi" }));
        // İkinci üstlenme 409 döner ve ikinci bir kayıt yazılmaz.
        Assert.Equal(HttpStatusCode.Conflict, (await api.Operator.PostAsJsonAsync($"/api/alerts/{alertId}/ack", new { note = "tekrar" })).StatusCode);

        var entries = (await Entries("action=AlertAcknowledged&pageSize=100")).Where(e => e.GetProperty("entityId").GetString() == alertId.ToString()).ToList();
        var entry = Assert.Single(entries);
        Assert.Equal("operator", entry.GetProperty("actor").GetString());
        Assert.Equal("Ekip yönlendirildi", entry.GetProperty("details").GetString());
    }

    [SqlFact]
    public async Task Denetim_kaydini_yalnizca_admin_gorur()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.Anonymous.GetAsync("/api/audit")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await api.Operator.GetAsync("/api/audit")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await api.Operator.GetAsync("/api/audit/export.xlsx")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await api.Admin.GetAsync("/api/audit")).StatusCode);
    }

    [SqlFact]
    public async Task Suzgec_ve_sayfalama_dogrulanir()
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await api.Admin.GetAsync("/api/audit?page=0")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await api.Admin.GetAsync("/api/audit?pageSize=101")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await api.Admin.GetAsync("/api/audit?from=2026-02-01&to=2026-01-01")).StatusCode);

        var none = await Body(await api.Admin.GetAsync($"/api/audit?actor={NewCode("yok")}"));
        Assert.Equal(0, none.GetProperty("total").GetInt32());

        var future = await Body(await api.Admin.GetAsync($"/api/audit?from={DateTime.UtcNow.AddDays(1):yyyy-MM-dd}"));
        Assert.Equal(0, future.GetProperty("total").GetInt32());

        var one = await Body(await api.Admin.GetAsync("/api/audit?pageSize=1"));
        Assert.Equal(1, one.GetProperty("items").GetArrayLength());
        Assert.True(one.GetProperty("total").GetInt32() >= 1);
    }

    [SqlFact]
    public async Task Alarm_ve_denetim_Excel_cikti_verir()
    {
        var alerts = await api.Operator.GetAsync("/api/alerts/export.xlsx");
        Assert.Equal(HttpStatusCode.OK, alerts.StatusCode);
        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", alerts.Content.Headers.ContentType?.MediaType);
        Assert.Equal("alarmlar.xlsx", alerts.Content.Headers.ContentDisposition?.FileNameStar ?? alerts.Content.Headers.ContentDisposition?.FileName);
        using (var wb = new XLWorkbook(new MemoryStream(await alerts.Content.ReadAsByteArrayAsync())))
            Assert.Equal("Varlık", wb.Worksheet(1).Cell(1, 2).GetString());

        var audit = await api.Admin.GetAsync("/api/audit/export.xlsx");
        Assert.Equal(HttpStatusCode.OK, audit.StatusCode);
        using var wb2 = new XLWorkbook(new MemoryStream(await audit.Content.ReadAsByteArrayAsync()));
        var sheet = wb2.Worksheet(1);
        Assert.Equal("Kullanıcı", sheet.Cell(1, 3).GetString());
        Assert.False(sheet.Cell(2, 1).IsEmpty()); // giriş kayıtları en az bir satır sağlar
    }
}
