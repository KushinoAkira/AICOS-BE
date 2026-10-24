using AICOS.BuildingBlocks.Exceptions;
using AICOS.BuildingBlocks.Security;
using AICOS.IdentityOrganization.Api.Domain;
using AICOS.IdentityOrganization.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AICOS.IdentityOrganization.Api.Features.Authentication;

public sealed class SessionService(
    IdentityOrganizationDbContext dbContext,
    JwtTokenService tokenService,
    RefreshTokenFactory refreshTokenFactory,
    TimeProvider clock,
    ILogger<SessionService> logger)
{
    public async Task<RefreshResponse> RefreshAsync(
        RefreshRequest request,
        CancellationToken cancellationToken)
    {
        Validate(request.RefreshToken);
        var tokenHash = RefreshTokenFactory.Hash(request.RefreshToken);
        var session = await dbContext.RefreshSessions
            .SingleOrDefaultAsync(item => item.TokenHash == tokenHash, cancellationToken);
        var now = clock.GetUtcNow().UtcDateTime;
        if (session is null || !session.IsActive(now))
        {
            throw InvalidRefreshToken();
        }

        var assignment = await ActiveAssignment(session.UserId, session.BranchId)
            .SingleOrDefaultAsync(cancellationToken);
        if (assignment is null)
        {
            session.Revoke(now);
            await dbContext.SaveChangesAsync(cancellationToken);
            throw InvalidRefreshToken();
        }

        var role = AuthenticationService.MapRole(assignment.TenantUser.Role.Code);
        var replacement = refreshTokenFactory.Create(session.UserId, session.BranchId);
        session.Revoke(now, replacement.Session.Id);
        dbContext.RefreshSessions.Add(replacement.Session);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException exception)
        {
            logger.LogWarning(exception, "Concurrent refresh-token rotation was rejected for session {SessionId}", session.Id);
            throw InvalidRefreshToken();
        }

        var user = assignment.TenantUser.User;
        var (accessToken, expiresAtUtc) = tokenService.CreateAccessToken(
            user.Id,
            user.FullName,
            role,
            assignment.BranchId);

        logger.LogInformation(
            "Rotated refresh session {SessionId} for user {UserId} and branch {BranchId}",
            session.Id,
            user.Id,
            assignment.BranchId);

        return new RefreshResponse(
            accessToken,
            expiresAtUtc,
            replacement.PlainText,
            replacement.Session.ExpiresAtUtc,
            AuthenticationService.ToStaffProfile(assignment, role));
    }

    public async Task LogoutAsync(LogoutRequest request, CancellationToken cancellationToken)
    {
        Validate(request.RefreshToken);
        var tokenHash = RefreshTokenFactory.Hash(request.RefreshToken);
        var session = await dbContext.RefreshSessions
            .SingleOrDefaultAsync(item => item.TokenHash == tokenHash, cancellationToken);
        if (session is null || session.RevokedAtUtc is not null)
        {
            return;
        }

        session.Revoke(clock.GetUtcNow().UtcDateTime);
        await dbContext.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Revoked refresh session {SessionId}", session.Id);
    }

    public async Task<StaffProfileResponse> GetCurrentAsync(
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        if (currentUser.UserId is null || currentUser.BranchId is null)
        {
            throw new UnauthorizedException();
        }

        var assignment = await ActiveAssignment(currentUser.UserId.Value, currentUser.BranchId.Value)
            .AsNoTracking()
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw new UnauthorizedException("The current staff assignment is no longer active.");
        var role = AuthenticationService.MapRole(assignment.TenantUser.Role.Code);
        return AuthenticationService.ToStaffProfile(assignment, role);
    }

    private IQueryable<StaffAssignment> ActiveAssignment(Guid userId, Guid branchId) =>
        dbContext.StaffAssignments
            .Include(assignment => assignment.Branch)
            .Include(assignment => assignment.TenantUser)
                .ThenInclude(membership => membership.User)
            .Include(assignment => assignment.TenantUser)
                .ThenInclude(membership => membership.Role)
            .Where(assignment =>
                assignment.BranchId == branchId &&
                assignment.TenantUser.UserId == userId &&
                assignment.Status == IdentityStatuses.Active &&
                assignment.Branch.Status == IdentityStatuses.Active &&
                assignment.TenantUser.Status == IdentityStatuses.Active &&
                assignment.TenantUser.User.Status == IdentityStatuses.Active);

    private static void Validate(string refreshToken)
    {
        if (string.IsNullOrWhiteSpace(refreshToken) || refreshToken.Length > 512)
        {
            throw new ValidationFailedException("refreshToken", "A refresh token is required.");
        }
    }

    private static UnauthorizedException InvalidRefreshToken() =>
        new("The refresh token is invalid or expired.", "auth.invalid_refresh_token");
}
