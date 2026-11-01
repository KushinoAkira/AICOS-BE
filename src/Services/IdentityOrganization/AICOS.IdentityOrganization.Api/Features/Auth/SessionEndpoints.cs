using AICOS.BuildingBlocks.Exceptions;
using AICOS.BuildingBlocks.Security;
using AICOS.IdentityOrganization.Api.Domain;
using AICOS.IdentityOrganization.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AICOS.IdentityOrganization.Api.Features.Auth;

public sealed record SessionResponse(Guid Id, string? Device, string? IpAddress, DateTime CreatedAtUtc, DateTime LastActiveAtUtc, DateTime ExpiresAtUtc, bool IsCurrent);

public sealed record RevokedSessionsResponse(int Revoked);

/// <summary>
/// The devices a person is signed in on. Routes: /api/v1/identity/auth/sessions. Only ever the caller's own sessions. Revoking stops the refresh token at once;
/// an access token already handed out keeps working until it expires, because services validate it without asking Identity.
/// </summary>
public static class SessionEndpoints
{
    public static void MapSessionEndpoints(this RouteGroupBuilder auth)
    {
        var sessions = auth.MapGroup("/sessions").RequireAuthorization();

        sessions.MapGet("/", ListAsync);
        sessions.MapDelete("/{id:guid}", RevokeAsync);
        sessions.MapPost("/revoke-others", RevokeOthersAsync);
    }

    private static async Task<List<SessionResponse>> ListAsync(
        HttpContext http, IdentityOrganizationDbContext db, ICurrentUser user, TimeProvider clock, CancellationToken ct)
    {
        var userId = UserId(user);
        var current = CurrentSession(http);
        var now = clock.GetUtcNow().UtcDateTime;
        return await db.UserSessions.AsNoTracking()
            .Where(s => s.UserId == userId && s.RevokedAtUtc == null && s.ExpiresAtUtc > now)
            .OrderByDescending(s => s.LastActiveAtUtc)
            .Select(s => new SessionResponse(s.Id, s.DeviceLabel, s.IpAddress, s.CreatedAtUtc, s.LastActiveAtUtc, s.ExpiresAtUtc, s.Id == current))
            .ToListAsync(ct);
    }

    private static async Task<IResult> RevokeAsync(Guid id, IdentityOrganizationDbContext db, ICurrentUser user, TimeProvider clock, CancellationToken ct)
    {
        var userId = UserId(user);
        var session = await db.UserSessions.FirstOrDefaultAsync(s => s.Id == id && s.UserId == userId, ct)
            ?? throw new NotFoundException("Session", id);
        session.Revoke(clock.GetUtcNow().UtcDateTime);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<RevokedSessionsResponse> RevokeOthersAsync(
        HttpContext http, IdentityOrganizationDbContext db, ICurrentUser user, TimeProvider clock, CancellationToken ct)
    {
        var userId = UserId(user);
        var current = CurrentSession(http);
        var others = await db.UserSessions.Where(s => s.UserId == userId && s.RevokedAtUtc == null && s.Id != current).ToListAsync(ct);
        var now = clock.GetUtcNow().UtcDateTime;
        others.ForEach(s => s.Revoke(now));
        await db.SaveChangesAsync(ct);
        return new RevokedSessionsResponse(others.Count);
    }

    private static Guid UserId(ICurrentUser user) => user.UserId ?? throw new UnauthorizedException("Not signed in.");

    private static Guid CurrentSession(HttpContext http) =>
        Guid.TryParse(http.User.FindFirst(AicosClaimTypes.SessionId)?.Value, out var id) ? id : Guid.Empty;
}
