using AssetPulse.Api.Domain;
using AssetPulse.Api.Security;

namespace AssetPulse.Tests;

public class RuleEngineTests
{
    private static AlertRule Upper(int consecutive = 1, decimal hysteresis = 0) =>
        new(1, 1, RuleKind.UpperLimit, 80, hysteresis, consecutive, Severity.Critical, 0);

    private static AlertRule Lower(int consecutive = 1, decimal hysteresis = 0) =>
        new(2, 1, RuleKind.LowerLimit, 20, hysteresis, consecutive, Severity.Warning, 0);

    /// <summary>Değerleri sırayla uygular; alarmın açılıp kapandığı noktaları döndürür.</summary>
    private static List<(int Index, Transition T)> Run(AlertRule rule, params decimal[] values)
    {
        var events = new List<(int, Transition)>();
        int streak = 0;
        bool open = false;
        for (var i = 0; i < values.Length; i++)
        {
            var step = RuleEngine.Next(rule, streak, open, values[i]);
            streak = step.Streak;
            if (step.Transition == Transition.Open) open = true;
            if (step.Transition == Transition.Clear) open = false;
            if (step.Transition != Transition.None) events.Add((i, step.Transition));
        }
        return events;
    }

    [Theory]
    [InlineData(RuleKind.UpperLimit, 80, 80.01, true)]
    [InlineData(RuleKind.UpperLimit, 80, 80, false)] // eşiğin kendisi ihlal sayılmaz
    [InlineData(RuleKind.LowerLimit, 20, 19.99, true)]
    [InlineData(RuleKind.LowerLimit, 20, 20, false)]
    public void IsViolation_esik_siniri(RuleKind kind, double threshold, double value, bool expected) =>
        Assert.Equal(expected, RuleEngine.IsViolation(kind, (decimal)threshold, (decimal)value));

    [Fact]
    public void Tek_ihlalde_alarm_acilir_normale_donunce_kapanir()
    {
        var events = Run(Upper(), 70, 85, 86, 75);
        Assert.Equal([(1, Transition.Open), (3, Transition.Clear)], events);
    }

    [Fact]
    public void Art_arda_ihlal_sayisi_dolmadan_alarm_acilmaz()
    {
        // 3 ardışık ihlal gerekir: araya giren normal değer sayacı sıfırlar.
        var events = Run(Upper(consecutive: 3), 85, 85, 70, 85, 85, 85);
        Assert.Equal([(5, Transition.Open)], events);
    }

    [Fact]
    public void Acik_alarm_varken_yeni_alarm_acilmaz()
    {
        var events = Run(Upper(consecutive: 2), 90, 90, 91, 92, 93);
        Assert.Single(events);
    }

    [Fact]
    public void Histerezis_bandinda_alarm_kapanmaz()
    {
        // Eşik 80, histerezis 5: 76 hâlâ bandın içinde, 75 ve altı kapatır.
        var events = Run(Upper(hysteresis: 5), 85, 78, 76, 80.5m, 75);
        Assert.Equal([(0, Transition.Open), (4, Transition.Clear)], events);
    }

    [Fact]
    public void Alt_sinir_kurali_ters_yonde_calisir()
    {
        var events = Run(Lower(hysteresis: 2), 25, 19, 21, 22, 23);
        Assert.Equal([(1, Transition.Open), (3, Transition.Clear)], events);
    }

    [Fact]
    public void Histerezis_bandindan_sonra_yeniden_ihlal_ayni_alarmi_surdurur()
    {
        var events = Run(Upper(hysteresis: 5), 85, 77, 90, 91);
        Assert.Equal([(0, Transition.Open)], events);
    }

    [Theory]
    [InlineData(0, Severity.Info, false, Health.NoData)]
    [InlineData(0, Severity.Info, true, Health.Normal)]
    [InlineData(1, Severity.Warning, true, Health.Warning)]
    [InlineData(2, Severity.Critical, true, Health.Critical)]
    [InlineData(3, Severity.Critical, false, Health.Critical)] // veri kesilse bile açık kritik alarm öncelikli
    public void Saglik_durumu_onceligi(int open, Severity max, bool fresh, Health expected)
    {
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        DateTime? last = fresh ? now.AddMinutes(-5) : null;
        Assert.Equal(expected, RuleEngine.ComputeHealth(open, max, last, now, TimeSpan.FromMinutes(30)));
    }

    [Fact]
    public void Eski_olcum_veri_yok_sayilir()
    {
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        Assert.Equal(Health.NoData, RuleEngine.ComputeHealth(0, Severity.Info, now.AddMinutes(-31), now, TimeSpan.FromMinutes(30)));
        Assert.Equal(Health.Normal, RuleEngine.ComputeHealth(0, Severity.Info, now.AddMinutes(-29), now, TimeSpan.FromMinutes(30)));
    }
}

public class ApiKeysTests
{
    [Fact]
    public void Uretilen_anahtarlar_benzersiz_ve_ozet_tutarlidir()
    {
        var (k1, h1) = ApiKeys.Generate();
        var (k2, _) = ApiKeys.Generate();
        Assert.NotEqual(k1, k2);
        Assert.StartsWith(ApiKeys.Prefix, k1);
        Assert.Equal(h1, ApiKeys.Hash(k1));
        Assert.Equal(64, h1.Length);
        Assert.DoesNotContain(k1, h1);
    }
}

public class CsvTests
{
    [Theory]
    [InlineData("Trafo", "Trafo")]
    [InlineData("a,b", "\"a,b\"")]
    [InlineData("=CMD()", "'=CMD()")]
    [InlineData("@x", "'@x")]
    [InlineData("", "")]
    public void Hucre_kacisi_ve_formul_korumasi(string input, string expected) =>
        Assert.Equal(expected, AssetPulse.Api.Endpoints.Helpers.CsvCell(input));
}
