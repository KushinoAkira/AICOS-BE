using AICOS.BuildingBlocks.Exceptions;
using AICOS.BuildingBlocks.Security;
using AICOS.WorkforceScheduling.Api.Domain;
using AICOS.WorkforceScheduling.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AICOS.WorkforceScheduling.Api.Infrastructure;

/// <summary>
/// Shift times are wall-clock times of the shop (Vietnam). Converts between UTC (stored, TimeProvider) and shop time.
/// Configure with <c>Workforce:TimeZone</c> (IANA id, default Asia/Ho_Chi_Minh).
/// </summary>
public sealed class ShopClock(TimeProvider clock, IConfiguration configuration)
{
    private readonly TimeZoneInfo _zone = TimeZoneInfo.FindSystemTimeZoneById(configuration["Workforce:TimeZone"] ?? "Asia/Ho_Chi_Minh");

    public DateTime UtcNow => clock.GetUtcNow().UtcDateTime;

    public DateTime LocalNow => TimeZoneInfo.ConvertTimeFromUtc(UtcNow, _zone);

    public DateOnly Today => DateOnly.FromDateTime(LocalNow);

    public DateTime ToUtc(DateOnly date, TimeOnly time) =>
        TimeZoneInfo.ConvertTimeToUtc(date.ToDateTime(time, DateTimeKind.Unspecified), _zone);
}

internal static class EmployeeLookup
{
    /// <summary>The employee profile behind the caller token (404 when the account has no HR profile yet).</summary>
    public static async Task<Employee> CurrentAsync(WorkforceSchedulingDbContext db, ICurrentUser user, CancellationToken ct)
    {
        var userId = user.UserId ?? throw new UnauthorizedException("Not signed in.");
        var tenantId = TenantScope.Require(user);
        return await db.Employees.FirstOrDefaultAsync(e => e.UserId == userId && e.TenantId == tenantId, ct)
            ?? throw new NotFoundException("Employee profile for user", userId);
    }
}

/// <summary>Branch scoping for reads: a BrandAdmin may omit the branch to cover the whole brand; everyone else works on their own branch.</summary>
internal static class WorkforceScope
{
    public static async Task<Guid?> FilterAsync(ICurrentUser user, IBranchDirectory directory, Guid? requestedBranchId, CancellationToken ct)
    {
        if (user.IsInRole(AicosRoles.BrandAdmin) && requestedBranchId is null)
        {
            return null;
        }

        return await BranchScope.ResolveAsync(user, directory, requestedBranchId, requireActive: false, ct);
    }
}
