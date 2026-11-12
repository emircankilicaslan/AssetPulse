using System.Globalization;
using AssetPulse.Web.Api;

namespace AssetPulse.Web.Helpers;

/// <summary>Arayüzde gösterim yardımcıları: Türkçe etiketler, tarih/sayı biçimleri, renk sınıfları.</summary>
public static class Display
{
    /// <summary>Gösterilen saatler API'nin UTC değerlerine bu ofset eklenerek hesaplanır (Türkiye: UTC+3, yaz/kış saati yok).</summary>
    public static TimeSpan UtcOffset { get; set; } = TimeSpan.FromHours(3);

    private static readonly CultureInfo Tr = CultureInfo.GetCultureInfo("tr-TR");

    public static string Local(DateTime? utc, string format = "dd.MM.yyyy HH:mm") =>
        utc is null ? "—" : (DateTime.SpecifyKind(utc.Value, DateTimeKind.Utc) + UtcOffset).ToString(format, Tr);

    /// <summary>"3 dk önce", "2 sa önce", "5 gün önce".</summary>
    public static string Ago(DateTime? utc, DateTime? nowUtc = null)
    {
        if (utc is null) return "veri yok";
        var span = (nowUtc ?? DateTime.UtcNow) - utc.Value;
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        if (span.TotalSeconds < 60) return "az önce";
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes} dk önce";
        if (span.TotalHours < 24) return $"{(int)span.TotalHours} sa önce";
        return $"{(int)span.TotalDays} gün önce";
    }

    /// <summary>Süre: "12 dk", "3 sa 20 dk", "2 gün".</summary>
    public static string Duration(DateTime fromUtc, DateTime? toUtc, DateTime? nowUtc = null)
    {
        var span = (toUtc ?? nowUtc ?? DateTime.UtcNow) - fromUtc;
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        if (span.TotalMinutes < 1) return "1 dk'dan az";
        if (span.TotalHours < 1) return $"{(int)span.TotalMinutes} dk";
        if (span.TotalDays < 1) return $"{(int)span.TotalHours} sa {span.Minutes} dk";
        return $"{(int)span.TotalDays} gün {span.Hours} sa";
    }

    public static string Number(decimal? value) =>
        value is null ? "—" : value.Value.ToString("#,0.##", Tr);

    public static string HealthLabel(string health) => health switch
    {
        "Critical" => "Kritik", "Warning" => "Uyarı", "NoData" => "Veri yok", "Normal" => "Normal", _ => health,
    };

    public static string HealthBadge(string health) => health switch
    {
        "Critical" => "text-bg-danger", "Warning" => "text-bg-warning", "NoData" => "text-bg-secondary", "Normal" => "text-bg-success",
        _ => "text-bg-light",
    };

    public static string SeverityLabel(string severity) => severity switch
    {
        "Critical" => "Kritik", "Warning" => "Uyarı", "Info" => "Bilgi", _ => severity,
    };

    public static string SeverityBadge(string severity) => severity switch
    {
        "Critical" => "text-bg-danger", "Warning" => "text-bg-warning", _ => "text-bg-info",
    };

    public static string StatusLabel(string status) => status switch
    {
        "Active" => "Aktif", "Acknowledged" => "Üstlenildi", "Cleared" => "Kapandı", _ => status,
    };

    public static string StatusBadge(string status) => status switch
    {
        "Active" => "text-bg-danger", "Acknowledged" => "text-bg-primary", "Cleared" => "text-bg-success", _ => "text-bg-light",
    };

    public static string CategoryLabel(string category) => category switch
    {
        "Transformer" => "Transformatör", "Lighting" => "Aydınlatma", "Signalling" => "Sinyalizasyon", "Pump" => "Pompa",
        "Generator" => "Jeneratör", _ => "Diğer",
    };

    public static readonly IReadOnlyList<(string Value, string Label)> Categories =
    [
        ("Transformer", "Transformatör"), ("Lighting", "Aydınlatma"), ("Signalling", "Sinyalizasyon"),
        ("Pump", "Pompa"), ("Generator", "Jeneratör"), ("Other", "Diğer"),
    ];

    /// <summary>"> 85 °C" veya "< 20 %" biçiminde eşik gösterimi.</summary>
    public static string Limit(string kind, decimal threshold, string unit) =>
        $"{(kind == "UpperLimit" ? ">" : "<")} {Number(threshold)} {unit}";

    public static string RuleText(RuleItem rule, string unit)
    {
        var text = $"{Limit(rule.Kind, rule.Threshold, unit)} · {rule.ConsecutiveCount} art arda";
        return rule.Hysteresis > 0 ? $"{text} · histerezis {Number(rule.Hysteresis)}" : text;
    }
}
