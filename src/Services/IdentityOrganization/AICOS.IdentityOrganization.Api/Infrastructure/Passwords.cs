using System.Security.Cryptography;
using System.Text;
using AICOS.IdentityOrganization.Api.Domain;
using Microsoft.AspNetCore.Identity;

namespace AICOS.IdentityOrganization.Api.Infrastructure;

/// <summary>BR-03 / NFR-03: passwords are hashed with ASP.NET Core Identity's PBKDF2 (salted, 100k iterations).</summary>
public sealed class PasswordService
{
    private readonly PasswordHasher<User> _hasher = new();

    public string Hash(User user, string password) => _hasher.HashPassword(user, password);

    /// <returns>Verified flag, plus a new hash when the stored one uses outdated parameters.</returns>
    public (bool Verified, string? Rehashed) Verify(User user, string password)
    {
        var result = _hasher.VerifyHashedPassword(user, user.PasswordHash, password);
        return result switch
        {
            PasswordVerificationResult.Success => (true, null),
            PasswordVerificationResult.SuccessRehashNeeded => (true, _hasher.HashPassword(user, password)),
            _ => (false, null),
        };
    }
}

/// <summary>Random secrets (refresh tokens, OTP codes) and their SHA-256 hashes for storage.</summary>
public static class Secrets
{
    public static string NewRefreshToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(64)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string NewNumericCode(int digits = 6) =>
        RandomNumberGenerator.GetInt32(0, (int)Math.Pow(10, digits)).ToString($"D{digits}");

    public static string Sha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    public static bool HashEquals(string hashA, string hashB) =>
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(hashA), Encoding.ASCII.GetBytes(hashB));
}
