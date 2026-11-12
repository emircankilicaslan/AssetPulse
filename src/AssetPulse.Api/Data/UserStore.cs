using Dapper;
using Microsoft.AspNetCore.Identity;

namespace AssetPulse.Api.Data;

public sealed class UserStore(Db db)
{
    private static readonly PasswordHasher<string> Hasher = new();
    // Kullanıcı yokken de bir doğrulama yapılır; yanıt süresinden hesap varlığı anlaşılmasın.
    private static readonly string DummyHash = Hasher.HashPassword("-", "dummy-password");

    public async Task<UserRow?> FindAsync(string username, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<UserRow>(new CommandDefinition(
            "SELECT Id, Username, PasswordHash, Role FROM dbo.Users WHERE Username = @username",
            new { username }, cancellationToken: ct));
    }

    public static bool Verify(UserRow? user, string password)
    {
        var result = Hasher.VerifyHashedPassword(user?.Username ?? "-", user?.PasswordHash ?? DummyHash, password);
        return user is not null && result != PasswordVerificationResult.Failed;
    }

    /// <summary>Kullanıcı yoksa oluşturur; varsa dokunmaz.</summary>
    public async Task EnsureAsync(string username, string password, string role, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            """
            IF NOT EXISTS (SELECT 1 FROM dbo.Users WHERE Username = @username)
                INSERT INTO dbo.Users (Username, PasswordHash, Role) VALUES (@username, @hash, @role)
            """,
            new { username, hash = Hasher.HashPassword(username, password), role }, cancellationToken: ct));
    }
}
