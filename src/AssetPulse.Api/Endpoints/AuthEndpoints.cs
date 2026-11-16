using AssetPulse.Api.Data;
using AssetPulse.Api.Security;
using AssetPulse.Api.Services;

namespace AssetPulse.Api.Endpoints;

public sealed record LoginRequest(string? Username, string? Password);

public sealed record LoginResponse(string Token, DateTime ExpiresAt, string Username, string Role);

public static class AuthEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapPost("/api/auth/login", async (LoginRequest req, UserStore users, TokenService tokens, AuditLog audit, CancellationToken ct) =>
            {
                if (string.IsNullOrWhiteSpace(req.Username) || string.IsNullOrEmpty(req.Password))
                    return Helpers.Invalid("credentials", "Kullanıcı adı ve parola zorunludur.");

                var user = await users.FindAsync(req.Username.Trim(), ct);
                if (!UserStore.Verify(user, req.Password))
                {
                    // Parola kayda girmez; yalnızca denenen kullanıcı adı yazılır.
                    await audit.RecordAsActorAsync(req.Username.Trim(), null, "LoginFailed", "User", null, null, ct);
                    return Results.Problem(title: "Kimlik doğrulanamadı", detail: "Kullanıcı adı veya parola hatalı.",
                        statusCode: StatusCodes.Status401Unauthorized);
                }

                await audit.RecordAsActorAsync(user!.Username, user.Role, "Login", "User", user.Id, null, ct);
                var (token, expires) = tokens.Create(user!);
                return Results.Ok(new LoginResponse(token, expires, user!.Username, user.Role));
            })
            .AllowAnonymous()
            .RequireRateLimiting("login")
            .WithTags("Kimlik")
            .WithSummary("Oturum açar ve JWT döndürür (IP başına dakikada 10 deneme).");
    }
}
