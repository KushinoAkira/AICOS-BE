using AICOS.BuildingBlocks.Exceptions;
using AICOS.BuildingBlocks.Security;
using AICOS.IdentityOrganization.Api.Domain;
using AICOS.IdentityOrganization.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AICOS.IdentityOrganization.Api.Features.Subscriptions;

/// <summary>What a tenant currently uses, measured against what its plan allows (BR-11).</summary>
public sealed record TenantUsage(int ActiveBranches, int StaffAccounts);

/// <summary>
/// Plan limits (FR-77, BR-11). A tenant without a subscription is not limited: the platform owner decides which
/// brands are on a plan. Only active branches and live staff accounts count; Customers and deleted accounts do not.
/// </summary>
internal static class SubscriptionRules
{
    private static readonly string[] StaffRoles =
        [AicosRoles.BrandAdmin, AicosRoles.BranchManager, AicosRoles.Barista, AicosRoles.Cashier];

    public static bool IsStaff(string role) => StaffRoles.Contains(role);

    public static async Task<TenantSubscription?> CurrentAsync(IdentityOrganizationDbContext db, Guid tenantId, CancellationToken ct) =>
        await db.TenantSubscriptions.FirstOrDefaultAsync(s => s.TenantId == tenantId && s.Status == SubscriptionStatus.Active, ct);

    public static async Task<TenantUsage> UsageAsync(IdentityOrganizationDbContext db, Guid tenantId, CancellationToken ct)
    {
        var branches = await db.Branches.CountAsync(b => b.TenantId == tenantId && b.Status == BranchStatus.Active, ct);
        var staff = await db.Users.CountAsync(u => u.TenantId == tenantId && StaffRoles.Contains(u.Role), ct);
        return new TenantUsage(branches, staff);
    }

    /// <summary>422 <c>subscription.limit_reached</c> when one more active branch would exceed the plan.</summary>
    public static async Task EnsureCanAddBranchAsync(IdentityOrganizationDbContext db, Guid tenantId, CancellationToken ct)
    {
        var plan = await PlanOfAsync(db, tenantId, ct);
        if (plan?.MaxBranches is not { } max)
        {
            return;
        }

        var branches = await db.Branches.CountAsync(b => b.TenantId == tenantId && b.Status == BranchStatus.Active, ct);
        if (branches >= max)
        {
            throw new BusinessRuleException(
                "subscription.limit_reached", $"Plan '{plan.Name}' allows at most {max} active branch(es). Upgrade the plan to add more.");
        }
    }

    /// <summary>422 <c>subscription.limit_reached</c> when one more staff account would exceed the plan.</summary>
    public static async Task EnsureCanAddStaffAsync(IdentityOrganizationDbContext db, Guid tenantId, string role, CancellationToken ct)
    {
        if (!StaffRoles.Contains(role))
        {
            return;
        }

        var plan = await PlanOfAsync(db, tenantId, ct);
        if (plan?.MaxStaffAccounts is not { } max)
        {
            return;
        }

        var staff = await db.Users.CountAsync(u => u.TenantId == tenantId && StaffRoles.Contains(u.Role), ct);
        if (staff >= max)
        {
            throw new BusinessRuleException(
                "subscription.limit_reached", $"Plan '{plan.Name}' allows at most {max} staff account(s). Upgrade the plan to add more.");
        }
    }

    /// <summary>
    /// Moving a tenant onto <paramref name="plan"/> is refused when it already uses more than the plan allows.
    /// (The rule for downgrades is an assumption: block rather than silently disable branches or accounts.)
    /// </summary>
    public static void EnsureUsageFits(SubscriptionPlan plan, TenantUsage usage)
    {
        var problems = new List<string>();
        if (plan.MaxBranches is { } maxBranches && usage.ActiveBranches > maxBranches)
        {
            problems.Add($"{usage.ActiveBranches} active branches (limit {maxBranches})");
        }

        if (plan.MaxStaffAccounts is { } maxStaff && usage.StaffAccounts > maxStaff)
        {
            problems.Add($"{usage.StaffAccounts} staff accounts (limit {maxStaff})");
        }

        if (problems.Count > 0)
        {
            throw new BusinessRuleException(
                "subscription.limits_exceeded",
                $"The tenant currently has {string.Join(" and ", problems)}, which exceeds plan '{plan.Name}'. Reduce usage first.");
        }
    }

    public static async Task<SubscriptionPlan?> PlanOfAsync(IdentityOrganizationDbContext db, Guid tenantId, CancellationToken ct)
    {
        var planId = await db.TenantSubscriptions
            .Where(s => s.TenantId == tenantId && s.Status == SubscriptionStatus.Active)
            .Select(s => (Guid?)s.PlanId)
            .FirstOrDefaultAsync(ct);

        return planId is null ? null : await db.SubscriptionPlans.AsNoTracking().FirstOrDefaultAsync(p => p.Id == planId, ct);
    }
}
