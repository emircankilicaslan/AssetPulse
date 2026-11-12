using System.Security.Cryptography;
using System.Text;

namespace AssetPulse.Api.Security;

/// <summary>
/// Cihaz (varlık) anahtarları. Anahtar yalnızca oluşturulurken bir kez gösterilir;
/// veritabanında yalnızca SHA-256 özeti tutulur. Anahtar 192 bit rastgeledir, bu yüzden
/// yavaş bir parola özeti yerine hızlı özet yeterlidir.
/// </summary>
public static class ApiKeys
{
    public const string Prefix = "ap_";

    public static (string Key, string Hash) Generate()
    {
        var key = Prefix + Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        return (key, Hash(key));
    }

    public static string Hash(string key) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();
}
