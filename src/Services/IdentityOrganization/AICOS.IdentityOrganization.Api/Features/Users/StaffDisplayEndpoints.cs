using AICOS.BuildingBlocks.Exceptions;
using AICOS.BuildingBlocks.Security;
using AICOS.IdentityOrganization.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AICOS.IdentityOrganization.Api.Features.Users;

/// <summary>Minimal staff display data, never a management user directory or customer PII.</summary>
public static class StaffDisplayEndpoints
{
    public static void MapStaffDisplayEndpoints(this RouteGroupBuilder api) =>
        api.MapGet("/staff/display-names", async (string ids, IdentityOrganizationDbContext db, ICurrentUser user, CancellationToken ct) =>
        {
            var tenant = TenantScope.Require(user);
            var parts = ids.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length is < 1 or > 50 || parts.Any(x => !Guid.TryParse(x, out _)))
                throw new ValidationFailedException("ids", "Supply 1-50 staff ids.");
            var wanted = parts.Select(Guid.Parse).Distinct().ToArray();
            var admin = user.IsInRole(AicosRoles.BrandAdmin);
            if (!admin && user.BranchId is null) throw new ForbiddenException("No branch assignment.");
            return await db.Users.AsNoTracking().Where(u => u.TenantId == tenant && wanted.Contains(u.Id)
                && (u.Role == AicosRoles.Cashier || u.Role == AicosRoles.Barista || u.Role == AicosRoles.BranchManager || u.Role == AicosRoles.BrandAdmin)
                && (admin || u.BranchId == user.BranchId || u.Role == AicosRoles.BrandAdmin))
                .Select(u => new { u.Id, u.FullName }).ToListAsync(ct);
        }).WithTags("Staff Display").RequireAuthorization(AicosPolicies.Staff);
}