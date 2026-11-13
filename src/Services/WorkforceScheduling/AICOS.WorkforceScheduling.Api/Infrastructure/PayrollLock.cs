using AICOS.BuildingBlocks.Exceptions;
using AICOS.WorkforceScheduling.Api.Domain;
using AICOS.WorkforceScheduling.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AICOS.WorkforceScheduling.Api.Infrastructure;

/// <summary>A finalized payroll period freezes its days: attendance and adjustments that change pay are refused until it is reopened.</summary>
internal static class PayrollLock
{
    public static async Task EnsureOpenAsync(WorkforceSchedulingDbContext db, Guid tenantId, Guid branchId, DateOnly day, CancellationToken ct)
    {
        var locked = await db.PayrollPeriods.AsNoTracking().AnyAsync(
            p => p.TenantId == tenantId && p.BranchId == branchId && p.Status != PayrollPeriodStatus.Reopened && p.From <= day && p.To >= day, ct);
        if (locked)
        {
            throw new ConflictException(
                $"The payroll that covers {day:yyyy-MM-dd} is finalized. Reopen the payroll period to change it, or add an adjustment to the next period.",
                "payroll.period_locked");
        }
    }
}
