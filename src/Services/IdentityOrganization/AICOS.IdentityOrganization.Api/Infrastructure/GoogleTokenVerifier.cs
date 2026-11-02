using System.IdentityModel.Tokens.Jwt;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace AICOS.IdentityOrganization.Api.Infrastructure;

/// <summary>Sign in with Google, section <c>Google</c> (environment variable <c>Google__ClientId</c>). Empty: Google sign-in is switched off.</summary>
public sealed class GoogleAuthOptions
{
    public const string SectionName = "Google";

    /// <summary>OAuth client id of the web app (Google Cloud Console, "Web application"). A token issued for another client is refused.</summary>
    public string ClientId { get; set; } = string.Empty;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ClientId);
}

/// <param name="Subject">Stable Google account id (the <c>sub</c> claim).</param>
public sealed record GoogleIdentity(string Subject, string Email, bool EmailVerified, string? Name);

public interface IGoogleTokenVerifier
{
    /// <returns>The identity inside a valid Google ID token, or null when the token is forged, expired or issued for another app.</returns>
    Task<GoogleIdentity?> VerifyAsync(string idToken, CancellationToken cancellationToken);
}

/// <summary>Checks the signature of a Google ID token against Google's published keys (cached), plus issuer, audience and expiry.</summary>
public sealed class GoogleTokenVerifier(
    HttpClient http, IMemoryCache cache, IOptions<GoogleAuthOptions> options, ILogger<GoogleTokenVerifier> logger) : IGoogleTokenVerifier
{
    private const string CertsUrl = "https://www.googleapis.com/oauth2/v3/certs";
    private static readonly string[] Issuers = ["https://accounts.google.com", "accounts.google.com"];

    public async Task<GoogleIdentity?> VerifyAsync(string idToken, CancellationToken cancellationToken)
    {
        var keys = await GetKeysAsync(cancellationToken);
        var parameters = new TokenValidationParameters
        {
            ValidIssuers = Issuers,
            ValidAudience = options.Value.ClientId,
            IssuerSigningKeys = keys,
            ClockSkew = TimeSpan.FromMinutes(1),
        };

        try
        {
            var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
            var principal = handler.ValidateToken(idToken, parameters, out _);
            var subject = principal.FindFirst("sub")?.Value;
            var email = principal.FindFirst("email")?.Value;
            if (string.IsNullOrEmpty(subject) || string.IsNullOrEmpty(email))
            {
                return null;
            }

            var verified = string.Equals(principal.FindFirst("email_verified")?.Value, "true", StringComparison.OrdinalIgnoreCase);
            return new GoogleIdentity(subject, email, verified, principal.FindFirst("name")?.Value);
        }
        catch (Exception ex) when (ex is SecurityTokenException or ArgumentException)
        {
            logger.LogWarning("A Google ID token was refused ({Reason}).", ex.GetType().Name);
            return null;
        }
    }

    private async Task<IReadOnlyList<SecurityKey>> GetKeysAsync(CancellationToken cancellationToken)
    {
        if (cache.TryGetValue(CertsUrl, out IReadOnlyList<SecurityKey>? cached) && cached is not null)
        {
            return cached;
        }

        var json = await http.GetStringAsync(CertsUrl, cancellationToken);
        IReadOnlyList<SecurityKey> keys = [.. new JsonWebKeySet(json).GetSigningKeys()];
        cache.Set(CertsUrl, keys, TimeSpan.FromHours(6));
        return keys;
    }
}
