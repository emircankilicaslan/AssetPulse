namespace AssetPulse.Api.Domain;

public enum Transition { None, Open, Clear }

public readonly record struct RuleStep(int Streak, Transition Transition);

/// <summary>
/// Alarm kurallarının saf (veritabanından bağımsız) değerlendirmesi.
/// Anlık dalgalanmalarda alarm üretmemek için iki mekanizma kullanılır:
/// art arda ihlal sayısı (<c>ConsecutiveCount</c>) ve kapanış için histerezis payı.
/// </summary>
public static class RuleEngine
{
    public static bool IsViolation(RuleKind kind, decimal threshold, decimal value) =>
        kind == RuleKind.UpperLimit ? value > threshold : value < threshold;

    /// <summary>Değer eşikten histerezis payı kadar güvenli tarafa döndüyse alarm kapanabilir.</summary>
    public static bool IsRecovered(RuleKind kind, decimal threshold, decimal hysteresis, decimal value) =>
        kind == RuleKind.UpperLimit ? value <= threshold - hysteresis : value >= threshold + hysteresis;

    /// <summary>Tek bir yeni ölçüm sonrası kuralın yeni ihlal sayacını ve alarm geçişini hesaplar.</summary>
    public static RuleStep Next(AlertRule rule, int streak, bool hasOpenAlert, decimal value)
    {
        if (IsViolation(rule.Kind, rule.Threshold, value))
        {
            var next = streak < int.MaxValue - 1 ? streak + 1 : streak;
            var open = !hasOpenAlert && next >= rule.ConsecutiveCount;
            return new RuleStep(next, open ? Transition.Open : Transition.None);
        }

        // İhlal yok: sayaç sıfırlanır. Açık alarm, değer histerezis bandından çıkınca kapanır.
        var clear = hasOpenAlert && IsRecovered(rule.Kind, rule.Threshold, rule.Hysteresis, value);
        return new RuleStep(0, clear ? Transition.Clear : Transition.None);
    }

    /// <summary>Varlığın genel sağlık durumu: kritik alarm > uyarı > veri yok > normal.</summary>
    public static Health ComputeHealth(
        int openAlerts, Severity maxOpenSeverity, DateTime? lastReadingUtc, DateTime nowUtc, TimeSpan staleAfter)
    {
        if (openAlerts > 0)
            return maxOpenSeverity == Severity.Critical ? Health.Critical : Health.Warning;
        if (lastReadingUtc is null || nowUtc - lastReadingUtc.Value > staleAfter)
            return Health.NoData;
        return Health.Normal;
    }
}
