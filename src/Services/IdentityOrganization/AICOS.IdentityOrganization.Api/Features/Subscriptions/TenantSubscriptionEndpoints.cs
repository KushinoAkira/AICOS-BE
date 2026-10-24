using AICOS.BuildingBlocks.Exceptions;
using AICOS.BuildingBlocks.Persistence;
using AICOS.BuildingBlocks.Security;
using AICOS.BuildingBlocks.Validation;
using AICOS.IdentityOrganization.Api.Domain;
using AICOS.IdentityOrganization.Api.Infrastructure;
using AICOS.IdentityOrganization.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AICOS.IdentityOrganization.Api.Features.Subscriptions;

/// <summary>
/// UC-07 Manage Tenant Subscriptions: put a tenant on a plan, record the payments received, renew, and watch renewal dates.
/// Routes: /api/v1/identity/tenants/{tenantId}/subscription... and /api/v1/identity/subscriptions. SuperAdmin only.
/// Billing is simulated (MVP): nothing is charged, the Super Admin records what was received.
/// </summary>
public static class TenantSubscriptionEndpoints
{
    private const int MaxPageSize = 100;

    public static void MapTenantSubscriptionEndpoints(this RouteGroupBuilder api)
    {
        var one = api.MapGroup("/tenants/{tenantId:guid}/subscription").WithTags("Tenant subscriptions")
            .RequireAuthorization(AicosPolicies.PlatformAdmin);

        one.MapGet("/", GetCurrentAsync);
        one.MapPost("/", AssignAsync);
        one.MapPost("/payments", RecordPaymentAsync);
        one.MapGet("/payments", ListPaymentsAsync);
        one.MapPost("/renew", RenewAsync);

        api.MapGet("/subscriptions", ListAsync).WithTags("Tenant subscriptions").RequireAuthorization(AicosPolicies.PlatformAdmin);
    }

    private static async Task<SubscriptionResponse> GetCurrentAsync(
        Guid tenantId, IdentityOrganizationDbContext db, IOptions<SubscriptionOptions> options, TimeProvider clock, CancellationToken ct)
    {
        var tenant = await FindTenantAsync(db, tenantId, ct);
        var sub = await SubscriptionRules.CurrentAsync(db, tenantId, ct) ?? throw new NotFoundException("Subscription of tenant", tenantId);
        return await ToResponseAsync(db, tenant, sub, options.Value, clock, ct);
    }

    /// <summary>Assign or change the plan. The previous subscription is kept as history. Refused if the tenant already uses more than the new plan allows.</summary>
    private static async Task<IResult> AssignAsync(
        Guid tenantId,
        AssignPlanRequest request,
        IdentityOrganizationDbContext db,
        AuditLogger audit,
        IOptions<SubscriptionOptions> options,
        TimeProvider clock,
        CancellationToken ct)
    {
        if (request.PlanId is null || request.PlanId == Guid.Empty)
        {
            throw new ValidationFailedException(nameof(request.PlanId), "planId is required.");
        }

        var tenant = await FindTenantAsync(db, tenantId, ct);
        var sub = await AssignPlanAsync(db, audit, tenant, request.PlanId.Value, request.StartDate, clock, ct);
        await db.SaveChangesAsync(ct);

        return Results.Created($"/api/v1/identity/tenants/{tenantId}/subscription", await ToResponseAsync(db, tenant, sub, options.Value, clock, ct));
    }

    /// <summary>
    /// Shared by POST /tenants/{id}/subscription and tenant onboarding. Adds the new subscription to the context (the caller saves).
    /// </summary>
    internal static async Task<TenantSubscription> AssignPlanAsync(
        IdentityOrganizationDbContext db,
        AuditLogger audit,
        Tenant tenant,
        Guid planId,
        DateTime? startDate,
        TimeProvider clock,
        CancellationToken ct)
    {
        var plan = await db.SubscriptionPlans.FirstOrDefaultAsync(p => p.Id == planId, ct)
            ?? throw new ValidationFailedException("PlanId", $"Plan '{planId}' does not exist.");
        if (!plan.IsActive)
        {
            throw new BusinessRuleException("subscription.plan_inactive", $"Plan '{plan.Name}' is deactivated and cannot be assigned.");
        }

        var current = await SubscriptionRules.CurrentAsync(db, tenant.Id, ct);
        if (current?.PlanId == plan.Id)
        {
            throw new ConflictException(
                $"The tenant is already on plan '{plan.Name}'. Renew the subscription instead.", "subscription.same_plan");
        }

        SubscriptionRules.EnsureUsageFits(plan, await SubscriptionRules.UsageAsync(db, tenant.Id, ct));

        var now = clock.GetUtcNow().UtcDateTime;
        var sub = new TenantSubscription(tenant.Id, plan, (startDate ?? now).ToUniversalTime());

        if (current is not null)
        {
            current.Supersede();
        }

        db.TenantSubscriptions.Add(sub);
        audit.Record(
            AuditScope.Platform, tenant.Id, "subscription.assigned", "Subscription", sub.Id,
            before: current is null ? null : Snapshot(current),
            after: Snapshot(sub));
        return sub;
    }

    private static async Task<IResult> RecordPaymentAsync(
        Guid tenantId,
        RecordPaymentRequest request,
        IdentityOrganizationDbContext db,
        ICurrentUser currentUser,
        AuditLogger audit,
        IOptions<SubscriptionOptions> options,
        TimeProvider clock,
        CancellationToken ct)
    {
        SubscriptionRequestValidator.EnsureValid(request);
        var now = clock.GetUtcNow().UtcDateTime;
        var paidAt = (request.PaidAt ?? now).ToUniversalTime();
        if (paidAt > now.AddDays(1))
        {
            throw new ValidationFailedException(nameof(request.PaidAt), "The payment date cannot be in the future.");
        }

        var tenant = await FindTenantAsync(db, tenantId, ct);
        var sub = await SubscriptionRules.CurrentAsync(db, tenantId, ct) ?? throw new NotFoundException("Subscription of tenant", tenantId);

        var before = Snapshot(sub);
        sub.AddPayment(request.Amount!.Value, paidAt);

        var payment = new SubscriptionPayment(
            sub.Id, tenantId, request.Amount.Value, paidAt, request.Method!, request.Reference, sub.PeriodStartUtc, currentUser.UserId);
        db.SubscriptionPayments.Add(payment);
        audit.Record(AuditScope.Platform, tenantId, "subscription.payment_recorded", "Subscription", sub.Id, before, Snapshot(sub));
        await db.SaveChangesAsync(ct);

        return Results.Created(
            $"/api/v1/identity/tenants/{tenantId}/subscription/payments",
            new { payment = SubscriptionPaymentResponse.From(payment), subscription = await ToResponseAsync(db, tenant, sub, options.Value, clock, ct) });
    }

    private static async Task<PagedResponse<SubscriptionPaymentResponse>> ListPaymentsAsync(
        Guid tenantId, IdentityOrganizationDbContext db, int page = 1, int pageSize = 20, CancellationToken ct = default)
    {
        await FindTenantAsync(db, tenantId, ct);
        return await db.SubscriptionPayments.AsNoTracking()
            .Where(p => p.TenantId == tenantId)
            .OrderByDescending(p => p.PaidAtUtc)
            .ToPagedAsync(page, pageSize, SubscriptionPaymentResponse.From, ct);
    }

    private static async Task<SubscriptionResponse> RenewAsync(
        Guid tenantId,
        IdentityOrganizationDbContext db,
        AuditLogger audit,
        IOptions<SubscriptionOptions> options,
        TimeProvider clock,
        CancellationToken ct)
    {
        var tenant = await FindTenantAsync(db, tenantId, ct);
        var sub = await SubscriptionRules.CurrentAsync(db, tenantId, ct) ?? throw new NotFoundException("Subscription of tenant", tenantId);

        var before = Snapshot(sub);
        sub.Renew(clock.GetUtcNow().UtcDateTime);
        audit.Record(AuditScope.Platform, tenantId, "subscription.renewed", "Subscription", sub.Id, before, Snapshot(sub));
        await db.SaveChangesAsync(ct);

        return await ToResponseAsync(db, tenant, sub, options.Value, clock, ct);
    }

    /// <summary>The platform owner's overview: every tenant's current subscription, filterable by renewal health and payment status.</summary>
    private static async Task<PagedResponse<SubscriptionSummaryResponse>> ListAsync(
        IdentityOrganizationDbContext db,
        IOptions<SubscriptionOptions> options,
        TimeProvider clock,
        SubscriptionHealth? health,
        SubscriptionPaymentStatus? paymentStatus,
        string? search,
        int page = 1,
        int pageSize = 20,
        CancellationToken ct = default)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);
        var settings = options.Value;
        var now = clock.GetUtcNow().UtcDateTime;

        var query = from s in db.TenantSubscriptions.AsNoTracking()
                    join t in db.Tenants.AsNoTracking() on s.TenantId equals t.Id
                    where s.Status == SubscriptionStatus.Active
                    select new { Sub = s, Tenant = t };

        if (paymentStatus is not null)
        {
            query = query.Where(x => x.Sub.PaymentStatus == paymentStatus);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLower();
            query = query.Where(x => x.Tenant.Name.ToLower().Contains(term) || x.Tenant.Code.Contains(term));
        }

        if (health is not null)
        {
            // The same boundaries as TenantSubscription.HealthAt, expressed on the renewal date so the database can filter.
            var graceStart = now.AddDays(-settings.GracePeriodDays);
            var noticeEnd = now.AddDays(settings.RenewalNoticeDays);
            query = health switch
            {
                SubscriptionHealth.Overdue => query.Where(x => x.Sub.PeriodEndUtc < graceStart),
                SubscriptionHealth.GracePeriod => query.Where(x => x.Sub.PeriodEndUtc < now && x.Sub.PeriodEndUtc >= graceStart),
                SubscriptionHealth.DueSoon => query.Where(x => x.Sub.PeriodEndUtc >= now && x.Sub.PeriodEndUtc <= noticeEnd),
                _ => query.Where(x => x.Sub.PeriodEndUtc > noticeEnd),
            };
        }

        var total = await query.CountAsync(ct);
        var rows = await query.OrderBy(x => x.Sub.PeriodEndUtc).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);

        var items = rows.Select(x => new SubscriptionSummaryResponse(
            x.Sub.Id, x.Tenant.Id, x.Tenant.Name, x.Tenant.Code, x.Sub.PlanName, x.Sub.Fee, x.Sub.BillingPeriod, x.Sub.PeriodEndUtc,
            x.Sub.PaymentStatus, x.Sub.Outstanding, x.Sub.HealthAt(now, settings.RenewalNoticeDays, settings.GracePeriodDays),
            DaysUntil(x.Sub.PeriodEndUtc, now))).ToList();

        return new PagedResponse<SubscriptionSummaryResponse>(items, page, pageSize, total);
    }

    internal static int DaysUntil(DateTime end, DateTime now) => (int)Math.Ceiling((end - now).TotalDays);

    internal static object Snapshot(TenantSubscription s) => new
    {
        s.PlanId, s.PlanName, s.Fee, BillingPeriod = s.BillingPeriod.ToString(), Status = s.Status.ToString(), s.PeriodStartUtc, s.PeriodEndUtc,
        PaymentStatus = s.PaymentStatus.ToString(), s.PaidAmount,
    };

    internal static async Task<SubscriptionResponse> ToResponseAsync(
        IdentityOrganizationDbContext db, Tenant tenant, TenantSubscription sub, SubscriptionOptions settings, TimeProvider clock, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var plan = await db.SubscriptionPlans.AsNoTracking().FirstAsync(p => p.Id == sub.PlanId, ct);
        var usage = await SubscriptionRules.UsageAsync(db, tenant.Id, ct);

        return new SubscriptionResponse(
            sub.Id, tenant.Id, tenant.Name, sub.PlanId, sub.PlanName, sub.Fee, sub.BillingPeriod, sub.Status, sub.PeriodStartUtc, sub.PeriodEndUtc,
            sub.PaymentStatus, sub.PaidAmount, sub.Outstanding, sub.PaidAtUtc,
            sub.HealthAt(now, settings.RenewalNoticeDays, settings.GracePeriodDays), DaysUntil(sub.PeriodEndUtc, now),
            usage.ActiveBranches, usage.StaffAccounts, plan.MaxBranches, plan.MaxStaffAccounts);
    }

    private static async Task<Tenant> FindTenantAsync(IdentityOrganizationDbContext db, Guid id, CancellationToken ct) =>
        await db.Tenants.FirstOrDefaultAsync(t => t.Id == id, ct) ?? throw new NotFoundException("Tenant", id);
}
