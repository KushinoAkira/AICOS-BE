using AICOS.BuildingBlocks.Security;
using AICOS.IdentityOrganization.Api.Domain;
using AICOS.IdentityOrganization.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AICOS.IdentityOrganization.Api.Infrastructure;

public sealed record IssuedTokens(string AccessToken, DateTime AccessTokenExpiresAtUtc, string RefreshToken, DateTime RefreshTokenExpiresAtUtc);

/// <summary>Creates access tokens (JWT, shared validation in every service) and refresh-token sessions.</summary>
public sealed class AuthTokenIssuer(
    JwtTokenService jwt, IdentityOrganizationDbContext db, IOptions<AuthOptions> options, TimeProvider clock)
{
    private readonly AuthOptions _options = options.Value;

    /// <summary>Starts a new session (login). Caller saves changes.</summary>
    public IssuedTokens StartSession(User user, HttpContext http)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var refreshToken = Secrets.NewRefreshToken();
        var expires = now.AddDays(_options.RefreshTokenDays);

        // Behind the gateway the connection comes from the gateway; the first forwarded address is the person's.
        var forwarded = http.Request.Headers["X-Forwarded-For"].ToString().Split(',')[0].Trim();
        var session = new UserSession(
            user.Id,
            Secrets.Sha256(refreshToken),
            http.Request.Headers.UserAgent.ToString(),
            forwarded.Length > 0 ? forwarded : http.Connection.RemoteIpAddress?.ToString(),
            now,
            expires);
        db.UserSessions.Add(session);

        return Issue(user, refreshToken, expires, session.Id);
    }

    /// <summary>Rotates the refresh token of an existing session (refresh). Caller saves changes.</summary>
    public IssuedTokens RotateSession(UserSession session, User user)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var refreshToken = Secrets.NewRefreshToken();
        var expires = now.AddDays(_options.RefreshTokenDays);

        session.Rotate(Secrets.Sha256(refreshToken), now, expires);
        return Issue(user, refreshToken, expires, session.Id, session.ActiveTenantId);
    }

    /// <summary>Signs the user out everywhere (password change/reset, lock, role change, delete). Caller saves changes.</summary>
    public async Task RevokeAllSessionsAsync(Guid userId, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var sessions = await db.UserSessions
            .Where(s => s.UserId == userId && s.RevokedAtUtc == null)
            .ToListAsync(ct);

        foreach (var session in sessions)
        {
            session.Revoke(now);
        }
    }

    private IssuedTokens Issue(User user, string refreshToken, DateTime refreshExpires, Guid sessionId, Guid? activeTenantId = null)
    {
        var (accessToken, accessExpires) = jwt.CreateAccessToken(user.Id, user.FullName, user.Role, user.BranchId, user.TenantId ?? activeTenantId, sessionId);
        return new IssuedTokens(accessToken, accessExpires, refreshToken, refreshExpires);
    }
}
