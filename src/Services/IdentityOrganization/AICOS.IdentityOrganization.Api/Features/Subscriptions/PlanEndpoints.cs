using AICOS.BuildingBlocks.Exceptions;
using AICOS.BuildingBlocks.Persistence;
using AICOS.BuildingBlocks.Security;
using AICOS.IdentityOrganization.Api.Domain;
using AICOS.IdentityOrganization.Api.Infrastructure;
using AICOS.IdentityOrganization.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AICOS.IdentityOrganization.Api.Features.Subscriptions;

/// <summary>
/// UC-06 Manage Subscription Plans. Routes: /api/v1/identity/subscription-plans. SuperAdmin only.
/// Plans are deactivated, never deleted: tenants already on a plan keep it (their fee and period were copied at assignment).
/// </summary>
public static class PlanEndpoints
{
    private const int MaxPageSize = 100;

    public static void MapPlanEndpoints(this RouteGroupBuilder api)
    {
        var plans = api.MapGroup("/subscription-plans").WithTags("Subscription plans").RequireAuthorization(AicosPolicies.PlatformAdmin);

        plans.MapGet("/", ListAsync);
        plans.MapGet("/{id:guid}", GetAsync).WithName("GetPlan");
        plans.MapPost("/", CreateAsync);
        plans.MapPut("/{id:guid}", UpdateAsync);
        plans.MapPatch("/{id:guid}/status", ChangeStatusAsync);
    }

    private static async Task<PagedResponse<PlanResponse>> ListAsync(
        IdentityOrganizationDbContext db, string? search, bool? isActive, int page = 1, int pageSize = 20, CancellationToken ct = default)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);

        var query = db.SubscriptionPlans.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLower();
            query = query.Where(p => p.Name.ToLower().Contains(term) || p.Tier.ToLower().Contains(term));
        }

        if (isActive is not null)
        {
            query = query.Where(p => p.IsActive == isActive);
        }

        var total = await query.CountAsync(ct);
        var plans = await query.OrderBy(p => p.Fee).ThenBy(p => p.Name).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        var counts = await SubscriberCountsAsync(db, plans.Select(p => p.Id).ToList(), ct);

        return new PagedResponse<PlanResponse>(plans.Select(p => PlanResponse.From(p, counts.GetValueOrDefault(p.Id))).ToList(), page, pageSize, total);
    }

    private static async Task<PlanResponse> GetAsync(Guid id, IdentityOrganizationDbContext db, CancellationToken ct)
    {
        var plan = await FindAsync(db, id, ct);
        return PlanResponse.From(plan, (await SubscriberCountsAsync(db, [id], ct)).GetValueOrDefault(id));
    }

    private static async Task<IResult> CreateAsync(PlanRequest request, IdentityOrganizationDbContext db, AuditLogger audit, CancellationToken ct)
    {
        SubscriptionRequestValidator.EnsureValid(request);
        await EnsureNameIsUniqueAsync(db, request.Name!, excludeId: null, ct);

        var plan = new SubscriptionPlan(
            request.Name!, request.Tier!, request.Fee!.Value, request.BillingPeriod!.Value, request.MaxBranches, request.MaxStaffAccounts, request.Description);
        db.SubscriptionPlans.Add(plan);
        audit.Record(AuditScope.Platform, null, "plan.created", "SubscriptionPlan", plan.Id, after: Snapshot(plan));
        await db.SaveChangesAsync(ct);

        return Results.CreatedAtRoute("GetPlan", new { id = plan.Id }, PlanResponse.From(plan, 0));
    }

    /// <summary>Editing a plan changes what new subscribers get and the limits of current ones; fees already agreed are not rewritten.</summary>
    private static async Task<PlanResponse> UpdateAsync(
        Guid id, PlanRequest request, IdentityOrganizationDbContext db, AuditLogger audit, CancellationToken ct)
    {
        SubscriptionRequestValidator.EnsureValid(request);
        var plan = await FindAsync(db, id, ct);
        await EnsureNameIsUniqueAsync(db, request.Name!, excludeId: id, ct);

        var before = Snapshot(plan);
        plan.Update(request.Name!, request.Tier!, request.Fee!.Value, request.BillingPeriod!.Value, request.MaxBranches, request.MaxStaffAccounts, request.Description);
        audit.Record(AuditScope.Platform, null, "plan.updated", "SubscriptionPlan", plan.Id, before, Snapshot(plan));
        await db.SaveChangesAsync(ct);

        return PlanResponse.From(plan, (await SubscriberCountsAsync(db, [id], ct)).GetValueOrDefault(id));
    }

    private static async Task<PlanResponse> ChangeStatusAsync(
        Guid id, PlanStatusRequest request, IdentityOrganizationDbContext db, AuditLogger audit, CancellationToken ct)
    {
        if (request.IsActive is null)
        {
            throw new ValidationFailedException(nameof(request.IsActive), "isActive is required (true or false).");
        }

        var plan = await FindAsync(db, id, ct);
        var before = Snapshot(plan);
        plan.SetActive(request.IsActive.Value);
        audit.Record(AuditScope.Platform, null, "plan.status_changed", "SubscriptionPlan", plan.Id, before, Snapshot(plan));
        await db.SaveChangesAsync(ct);

        return PlanResponse.From(plan, (await SubscriberCountsAsync(db, [id], ct)).GetValueOrDefault(id));
    }

    private static object Snapshot(SubscriptionPlan p) => new
    {
        p.Name, p.Tier, p.Fee, BillingPeriod = p.BillingPeriod.ToString(), p.MaxBranches, p.MaxStaffAccounts, p.IsActive,
    };

    private static async Task<SubscriptionPlan> FindAsync(IdentityOrganizationDbContext db, Guid id, CancellationToken ct) =>
        await db.SubscriptionPlans.FirstOrDefaultAsync(p => p.Id == id, ct) ?? throw new NotFoundException("Subscription plan", id);

    private static async Task<Dictionary<Guid, int>> SubscriberCountsAsync(IdentityOrganizationDbContext db, List<Guid> planIds, CancellationToken ct) =>
        await db.TenantSubscriptions.AsNoTracking()
            .Where(s => s.Status == SubscriptionStatus.Active && planIds.Contains(s.PlanId))
            .GroupBy(s => s.PlanId)
            .Select(g => new { PlanId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.PlanId, x => x.Count, ct);

    private static async Task EnsureNameIsUniqueAsync(IdentityOrganizationDbContext db, string name, Guid? excludeId, CancellationToken ct)
    {
        var normalized = name.Trim().ToLower();
        if (await db.SubscriptionPlans.AnyAsync(p => p.Name.ToLower() == normalized && p.Id != excludeId, ct))
        {
            throw new ConflictException($"A plan named '{name.Trim()}' already exists.", "plan.duplicate_name");
        }
    }
}
