using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using AssetPulse.Api.Data;
using Microsoft.IdentityModel.Tokens;

namespace AssetPulse.Api.Security;

public sealed class TokenService(IConfiguration configuration, TimeProvider clock)
{
    public const string Issuer = "AssetPulse";
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(8);

    private readonly SymmetricSecurityKey _key =
        new(Encoding.UTF8.GetBytes(configuration["Jwt:Secret"] ?? throw new InvalidOperationException("Jwt:Secret tanımlı değil.")));

    public static SymmetricSecurityKey SigningKey(string secret) => new(Encoding.UTF8.GetBytes(secret));

    public (string Token, DateTime ExpiresAt) Create(UserRow user)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var expires = now + Lifetime;
        var token = new JwtSecurityToken(
            issuer: Issuer, audience: Issuer,
            claims:
            [
                new Claim("sub", user.Id.ToString()),
                new Claim("name", user.Username),
                new Claim("role", user.Role),
            ],
            notBefore: now, expires: expires,
            signingCredentials: new SigningCredentials(_key, SecurityAlgorithms.HmacSha256));
        return (new JwtSecurityTokenHandler().WriteToken(token), expires);
    }
}
