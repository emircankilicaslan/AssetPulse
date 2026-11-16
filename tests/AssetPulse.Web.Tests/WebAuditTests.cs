using System.Net;
using AssetPulse.Web.Helpers;

namespace AssetPulse.Web.Tests;

/// <summary>Denetim kaydı sayfası ve Excel indirmeleri (API sahte sunucuyla değiştirilir).</summary>
public sealed class WebAuditTests : IDisposable
{
    private readonly WebFixture _web = new();
    private readonly HttpClient _client;

    public WebAuditTests()
    {
        _client = _web.NewClient();
        _web.Api.Json("GET api/dashboard", Sample.Dashboard());
        _web.Api.Json("GET api/assets", new[] { Sample.Asset() });
    }

    public void Dispose()
    {
        _client.Dispose();
        _web.Dispose();
    }

    private async Task LoginAsync(string role, string user)
    {
        _web.Api.Json("POST api/auth/login", Sample.Login(role, user));
        var page = await Html.BodyAsync(await _client.GetAsync("/Account/Login"));
        var res = await _client.PostAsync("/Account/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Username"] = user, ["Password"] = "x-parola-123", ["ReturnUrl"] = "",
            ["__RequestVerificationToken"] = Html.Token(page),
        }));
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
    }

    private static object AuditPage(params object[] items) => new { items, total = items.Length, page = 1, pageSize = 20 };

    private static object Entry(string actor, string action, string? details = null) => new
    {
        id = 1, occurredAt = DateTime.UtcNow.AddMinutes(-3), actor, actorRole = "Operator", action,
        entityType = "Alert", entityId = "5", details,
    };

    [Fact]
    public async Task Operator_denetim_kaydina_giremez()
    {
        await LoginAsync("Operator", "operator");
        var res = await _client.GetAsync("/Audit");
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.Contains("/Account/AccessDenied", res.Headers.Location!.ToString());
        Assert.DoesNotContain(_web.Api.Calls, c => c.Path == "api/audit");
    }

    [Fact]
    public async Task Admin_denetim_kaydini_turkce_etiketlerle_gorur_ve_suzgec_API_ya_iletilir()
    {
        _web.Api.Json("GET api/audit", AuditPage(Entry("operator", "AlertAcknowledged", "Ekip yönlendirildi")));
        await LoginAsync("Admin", "admin");

        var res = await _client.GetAsync("/Audit?actor=operator&islem=AlertAcknowledged");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var html = await Html.BodyAsync(res);
        Assert.Contains("Alarm üstlenildi", html);
        Assert.Contains("Ekip yönlendirildi", html);
        Assert.Contains("operator", html);

        var call = Assert.Single(_web.Api.Calls, c => c.Path == "api/audit");
        Assert.Contains("actor=operator", call.Query);
        Assert.Contains("action=AlertAcknowledged", call.Query);
        Assert.Equal("Bearer jwt-admin", call.Authorization);
    }

    [Fact]
    public async Task Denetim_kaydinda_kullanici_girdisi_kacislanir()
    {
        _web.Api.Json("GET api/audit", AuditPage(Entry("<script>alert(1)</script>", "LoginFailed")));
        await LoginAsync("Admin", "admin");

        var raw = await (await _client.GetAsync("/Audit")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("<script>alert(1)</script>", raw);
        Assert.Contains("&lt;script&gt;", raw);
    }

    [Fact]
    public async Task Denetim_kaydi_Excel_olarak_indirilir()
    {
        var payload = new byte[] { 0x50, 0x4B, 0x03, 0x04, 1, 2, 3 };
        _web.Api.Routes["GET api/audit/export.xlsx"] = () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) };
        await LoginAsync("Admin", "admin");

        var res = await _client.GetAsync("/Audit/Export?actor=admin");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", res.Content.Headers.ContentType?.MediaType);
        Assert.Equal(payload, await res.Content.ReadAsByteArrayAsync());
        Assert.Contains("actor=admin", Assert.Single(_web.Api.Calls, c => c.Path == "api/audit/export.xlsx").Query);
    }

    [Fact]
    public async Task Alarmlar_Excel_olarak_indirilir_ve_suzgec_iletilir()
    {
        var payload = new byte[] { 0x50, 0x4B, 0x03, 0x04 };
        _web.Api.Routes["GET api/alerts/export.xlsx"] = () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) };
        await LoginAsync("Operator", "operator");

        var res = await _client.GetAsync("/Alerts/ExportXlsx?status=open&severity=Critical");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(payload, await res.Content.ReadAsByteArrayAsync());
        var query = Assert.Single(_web.Api.Calls, c => c.Path == "api/alerts/export.xlsx").Query;
        Assert.Contains("open=true", query);
        Assert.Contains("severity=Critical", query);
    }

    [Theory]
    [InlineData("AlertAcknowledged", "Alarm üstlenildi")]
    [InlineData("LoginFailed", "Başarısız giriş")]
    [InlineData("BilinmeyenIslem", "BilinmeyenIslem")]
    public void Denetim_islem_etiketleri(string action, string label) => Assert.Equal(label, Display.AuditActionLabel(action));
}
