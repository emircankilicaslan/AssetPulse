using System.Net;
using AssetPulse.Web.Helpers;

namespace AssetPulse.Web.Tests;

/// <summary>Arayüz katmanı testleri: API sahte bir sunucuyla değiştirilir, gerçek HTTP hattı (çerez, CSRF, yetki) çalışır.</summary>
public sealed class WebTests : IDisposable
{
    private readonly WebFixture _web = new();
    private readonly HttpClient _client;

    public WebTests()
    {
        _client = _web.NewClient();
        _web.Api.Json("POST api/auth/login", Sample.Login());
        _web.Api.Json("GET api/dashboard", Sample.Dashboard());
        _web.Api.Json("GET api/assets", new[] { Sample.Asset() });
    }

    public void Dispose()
    {
        _client.Dispose();
        _web.Dispose();
    }

    private async Task<HttpResponseMessage> LoginAsync(string? returnUrl = null)
    {
        var page = await Html.BodyAsync(await _client.GetAsync("/Account/Login"));
        var form = new Dictionary<string, string>
        {
            ["Username"] = "operator", ["Password"] = "x-parola-123", ["ReturnUrl"] = returnUrl ?? "",
            ["__RequestVerificationToken"] = Html.Token(page),
        };
        return await _client.PostAsync("/Account/Login", new FormUrlEncodedContent(form));
    }

    private async Task<string> TokenFromAsync(string url) => Html.Token(await Html.BodyAsync(await _client.GetAsync(url)));

    [Fact]
    public async Task Girissiz_istek_giris_sayfasina_yonlenir()
    {
        var res = await _client.GetAsync("/");
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.Contains("/Account/Login", res.Headers.Location!.ToString());
    }

    [Fact]
    public async Task Giris_sayfasi_guvenlik_basliklarini_icerir()
    {
        var res = await _client.GetAsync("/Account/Login");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("script-src 'self'", res.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal("DENY", res.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal("nosniff", res.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.DoesNotContain("<script>", await res.Content.ReadAsStringAsync()); // inline betik yok
    }

    [Fact]
    public async Task Hatali_parola_sayfada_hata_gosterir_ve_oturum_acmaz()
    {
        _web.Api.Status("POST api/auth/login", HttpStatusCode.Unauthorized);
        var res = await LoginAsync();
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("Kullanıcı adı veya parola hatalı", await Html.BodyAsync(res));
        Assert.Equal(HttpStatusCode.Redirect, (await _client.GetAsync("/")).StatusCode);
    }

    [Fact]
    public async Task Basarili_giris_panel_sayfasini_acar_ve_token_API_ye_iletilir()
    {
        var login = await LoginAsync();
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        Assert.Equal("/", login.Headers.Location!.OriginalString);

        var res = await _client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var html = await Html.BodyAsync(res);
        Assert.Contains("Genel durum", html);
        Assert.Contains("Ankara TM-1 Güç Transformatörü", html);
        Assert.Contains("96,4 °C", html);          // tr-TR ondalık ayracı
        Assert.Contains("Üstlen", html);            // aktif alarm için düğme

        // Kullanıcının JWT'si API çağrısına Bearer olarak eklenmiş olmalı.
        Assert.Contains(_web.Api.Calls, c => c.Path == "api/dashboard" && c.Authorization == "Bearer jwt-operator");
    }

    [Fact]
    public async Task Giris_sonrasi_yalnizca_yerel_adrese_yonlenir()
    {
        var login = await LoginAsync("https://kotu-site.example/phishing");
        Assert.Equal("/", login.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task Antiforgery_belirteci_olmayan_POST_reddedilir()
    {
        var res = await _client.PostAsync("/Account/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Username"] = "operator", ["Password"] = "x-parola-123",
        }));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.DoesNotContain(_web.Api.Calls, c => c.Path == "api/auth/login");
    }

    [Fact]
    public async Task Token_suresi_dolunca_giris_sayfasina_donulur()
    {
        await LoginAsync();
        _web.Api.Status("GET api/dashboard", HttpStatusCode.Unauthorized);
        var res = await _client.GetAsync("/");
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.Contains("/Account/Login", res.Headers.Location!.ToString());
        Assert.Equal(HttpStatusCode.Redirect, (await _client.GetAsync("/Assets")).StatusCode); // çerez de temizlendi
    }

    [Fact]
    public async Task API_ulasilamazsa_503_sayfasi_gosterilir()
    {
        await LoginAsync();
        _web.Api.Throw("GET api/dashboard");
        var res = await _client.GetAsync("/");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, res.StatusCode);
        Assert.Contains("Servise ulaşılamıyor", await Html.BodyAsync(res));
    }

    [Fact]
    public async Task Operator_varlik_olusturma_sayfasina_giremez()
    {
        await LoginAsync();
        var res = await _client.GetAsync("/Assets/Create");
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.Contains("/Account/AccessDenied", res.Headers.Location!.ToString());
    }

    [Fact]
    public async Task Admin_varlik_olusturur_ve_API_anahtari_bir_kez_gosterilir()
    {
        _web.Api.Json("POST api/auth/login", Sample.Login("Admin", "admin"));
        _web.Api.Json("POST api/assets", new { id = 9, code = "TM-9", name = "Deneme", category = "Transformer", location = (string?)null, apiKey = "ap_gizli_anahtar_123" }, HttpStatusCode.Created);
        await LoginAsync();

        var token = await TokenFromAsync("/Assets/Create");
        var res = await _client.PostAsync("/Assets/Create", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Code"] = "TM-9", ["Name"] = "Deneme", ["Category"] = "Transformer", ["Location"] = "", ["__RequestVerificationToken"] = token,
        }));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("ap_gizli_anahtar_123", await Html.BodyAsync(res));
        Assert.Contains(_web.Api.Calls, c => c.Path == "api/assets" && c.Method == HttpMethod.Post && c.Authorization == "Bearer jwt-admin");
    }

    [Fact]
    public async Task Ayni_kodlu_varlikta_API_hatasi_formda_gosterilir()
    {
        _web.Api.Json("POST api/auth/login", Sample.Login("Admin", "admin"));
        _web.Api.Json("POST api/assets", new { title = "Çakışma", detail = "'TM-9' kodlu bir varlık zaten var." }, HttpStatusCode.Conflict);
        await LoginAsync();

        var token = await TokenFromAsync("/Assets/Create");
        var res = await _client.PostAsync("/Assets/Create", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Code"] = "TM-9", ["Name"] = "Deneme", ["Category"] = "Transformer", ["__RequestVerificationToken"] = token,
        }));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("zaten var", await Html.BodyAsync(res));
    }

    [Fact]
    public async Task Alarm_ustlenme_notla_birlikte_API_ye_gonderilir()
    {
        _web.Api.Json("POST api/alerts/5/ack", Sample.Alert(5, "Acknowledged"));
        await LoginAsync();

        var token = await TokenFromAsync("/");
        var res = await _client.PostAsync("/Alerts/Acknowledge", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["id"] = "5", ["note"] = "Ekip yönlendirildi", ["returnUrl"] = "/", ["__RequestVerificationToken"] = token,
        }));
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        var call = Assert.Single(_web.Api.Calls, c => c.Path == "api/alerts/5/ack");
        Assert.Equal("Ekip yönlendirildi", System.Text.Json.JsonDocument.Parse(call.Body!).RootElement.GetProperty("note").GetString());
        Assert.Equal("Bearer jwt-operator", call.Authorization);
    }

    [Fact]
    public async Task Alarm_listesi_durum_suzgecini_API_ye_aktarir()
    {
        _web.Api.Json("GET api/alerts", new { items = new[] { Sample.Alert() }, total = 1, page = 1, pageSize = 15 });
        await LoginAsync();
        var res = await _client.GetAsync("/Alerts?status=open&severity=Critical");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var call = Assert.Single(_web.Api.Calls, c => c.Path == "api/alerts");
        Assert.Contains("open=true", call.Query);
        Assert.Contains("severity=Critical", call.Query);
        Assert.Contains("1 kayıt", await Html.BodyAsync(res));
    }

    [Fact]
    public async Task Grafik_verisi_JSON_doner_oturumsuz_istek_girise_yonlenir()
    {
        _web.Api.Json("GET api/assets/1/sensors/oil-temp/series", new
        {
            sensor = "oil-temp", from = DateTime.UtcNow.AddDays(-1), to = DateTime.UtcNow, bucket = "Raw",
            points = new[] { new { time = DateTime.UtcNow.AddMinutes(-5), min = 60m, avg = 61.5m, max = 63m, count = 1 } },
        });

        var anonymous = new HttpRequestMessage(HttpMethod.Get, "/Assets/1/Series?code=oil-temp&range=24h");
        anonymous.Headers.Add("X-Requested-With", "fetch");
        Assert.Equal(HttpStatusCode.Redirect, (await _client.SendAsync(anonymous)).StatusCode); // girişsiz: oturum çerezi yok

        await LoginAsync();
        var req = new HttpRequestMessage(HttpMethod.Get, "/Assets/1/Series?code=oil-temp&range=24h");
        req.Headers.Add("X-Requested-With", "fetch");
        var res = await _client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("\"avg\":61.5", await res.Content.ReadAsStringAsync());
    }
}

public class DisplayTests
{
    private static readonly DateTime Now = new(2026, 11, 12, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(-20, "az önce")]
    [InlineData(-5 * 60, "5 dk önce")]
    [InlineData(-3 * 3600, "3 sa önce")]
    [InlineData(-2 * 86400, "2 gün önce")]
    public void Ago_gecen_sureyi_turkce_yazar(int seconds, string expected) =>
        Assert.Equal(expected, Display.Ago(Now.AddSeconds(seconds), Now));

    [Fact]
    public void Ago_veri_yoksa_veri_yok_der_gelecek_zamani_sifirlar()
    {
        Assert.Equal("veri yok", Display.Ago(null, Now));
        Assert.Equal("az önce", Display.Ago(Now.AddMinutes(10), Now));
    }

    [Fact]
    public void Yerel_saat_UTC_arti_3_tur()
    {
        Display.UtcOffset = TimeSpan.FromHours(3);
        Assert.Equal("12.11.2026 15:00", Display.Local(Now));
        Assert.Equal("—", Display.Local(null));
    }

    [Theory]
    [InlineData(30, "1 dk'dan az")]
    [InlineData(12 * 60, "12 dk")]
    [InlineData(3 * 3600 + 20 * 60, "3 sa 20 dk")]
    [InlineData(2 * 86400 + 5 * 3600, "2 gün 5 sa")]
    public void Sure_okunakli_yazilir(int seconds, string expected) =>
        Assert.Equal(expected, Display.Duration(Now, Now.AddSeconds(seconds)));

    [Fact]
    public void Sayilar_turkce_bicimlenir()
    {
        Assert.Equal("1.234,5", Display.Number(1234.5m));
        Assert.Equal("85", Display.Number(85.00m));
        Assert.Equal("—", Display.Number(null));
    }

    [Fact]
    public void Esik_ve_kural_metinleri()
    {
        Assert.Equal("> 85 °C", Display.Limit("UpperLimit", 85, "°C"));
        Assert.Equal("< 6,2 A", Display.Limit("LowerLimit", 6.2m, "A"));
        var rule = new AssetPulse.Web.Api.RuleItem(1, 1, "UpperLimit", 85, 3, 3, "Critical", true);
        Assert.Equal("> 85 °C · 3 art arda · histerezis 3", Display.RuleText(rule, "°C"));
    }

    [Theory]
    [InlineData("Critical", "Kritik", "text-bg-danger")]
    [InlineData("Warning", "Uyarı", "text-bg-warning")]
    [InlineData("NoData", "Veri yok", "text-bg-secondary")]
    [InlineData("Normal", "Normal", "text-bg-success")]
    public void Saglik_etiketi_ve_rengi(string health, string label, string css)
    {
        Assert.Equal(label, Display.HealthLabel(health));
        Assert.Equal(css, Display.HealthBadge(health));
    }
}
