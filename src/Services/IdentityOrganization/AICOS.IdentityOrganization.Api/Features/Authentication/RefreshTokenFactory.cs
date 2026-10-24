using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;
using AICOS.IdentityOrganization.Api.Domain;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;

namespace AICOS.IdentityOrganization.Api.Features.Authentication;

public sealed class RefreshTokenOptions
{
    public const string SectionName = "RefreshToken";

    [Range(1, 90)]
    public int ExpiryDays { get; set; } = 30;
}

public sealed record IssuedRefreshToken(string PlainText, RefreshSession Session);

public sealed class RefreshTokenFactory(IOptions<RefreshTokenOptions> options, TimeProvider clock)
{
    private readonly RefreshTokenOptions _options = options.Value;

    public IssuedRefreshToken Create(Guid userId, Guid branchId)
    {
        var plainText = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(64));
        var now = clock.GetUtcNow().UtcDateTime;
        var session = new RefreshSession(
            Guid.NewGuid(),
            userId,
            branchId,
            Hash(plainText),
            now.AddDays(_options.ExpiryDays),
            now);
        return new IssuedRefreshToken(plainText, session);
    }

    public static string Hash(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
