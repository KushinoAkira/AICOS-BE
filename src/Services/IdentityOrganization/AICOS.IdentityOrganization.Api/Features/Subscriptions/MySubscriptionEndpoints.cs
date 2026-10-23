using AICOS.BuildingBlocks.Exceptions;
using AICOS.BuildingBlocks.Persistence;
using AICOS.BuildingBlocks.Security;
using AICOS.IdentityOrganization.Api.Domain;
using AICOS.IdentityOrganization.Api.Infrastructure;
using AICOS.IdentityOrganization.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AICOS.IdentityOrganization.Api.Features.Subscriptions;

/// <summary>
/// The subscription as its own Brand Admin sees it (SCRUM-132, UC-22, FR-20). <see cref="AmountToPay"/> is what a simulated payment would
/// settle now: the outstanding amount of the current period, or the fee of the next period when the current one is paid and renewal is open.
/// </summary>
public sealed record MySubscriptionResponse(SubscriptionResponse Subscription, bool CanRenew, DateTime? RenewableFromUtc, decimal AmountToPay);

/// <summary>What a simulated renewal did: <c>PaidOutstanding</c> (settled the current period) or <c>Renewed</c> (started and paid the next period).</summary>
public sealed record RenewalResult(string Action, decimal AmountPaid, SubscriptionPaymentResponse Payment, MySubscriptionResponse Subscription);

/// <summary>
/// UC-22 View Subscription Status and Renew (simulated). Routes: /api/v1/identity/subscription. BrandAdmin only, always about their own brand.
/// No money moves (billing is simulated in the MVP): a renewal records a payment with method "Simulated" and is kept in the audit history.
/// The Super Admin still manages plans and records real payments from <c>/tenants/{id}/subscription</c>.
/// </summary>
public static class MySubscriptionEndpoints
{
    public const string SimulatedMethod = "Simulated";

    public static void MapMySubscriptionEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/subscription").WithTags("My subscription").RequireAuthorization(AicosPolicies.AdminOnly);

        group.MapGet("/", GetAsync);
        group.MapGet("/payments", PaymentsAsync);
        group.MapPost("/renew", RenewAsync);
    }

    private static async Task<MySubscriptionResponse> GetAsync(
        IdentityOrganizationDbContext db, ICurrentUser user, IOptions<SubscriptionOptions> options, TimeProvider clock, CancellationToken ct)
    {
        var (tenant, sub) = await LoadAsync(db, user, ct);
        return await BuildAsync(db, tenant, sub, options.Value, clock, ct);
    }

    private static async Task<PagedResponse<SubscriptionPaymentResponse>> PaymentsAsync(
        IdentityOrganizationDbContext db, ICurrentUser user, int page = 1, int pageSize = 20, CancellationToken ct = default)
    {
        var tenantId = TenantScope.Require(user);
        return await db.SubscriptionPayments.AsNoTracking()
            .Where(p => p.TenantId == tenantId)
            .OrderByDescending(p => p.PaidAtUtc)
            .ToPagedAsync(page, pageSize, SubscriptionPaymentResponse.From, ct);
    }

    /// <summary>
    /// Simulated renewal. With an unpaid period it pays what is outstanding. With a paid period it starts the next one and pays it, but only
    /// once renewal is open (inside the notice window, or after the end), so a brand cannot buy periods far ahead.
    /// </summary>
    private static async Task<RenewalResult> RenewAsync(
        IdentityOrganizationDbContext db,
        ICurrentUser user,
        AuditLogger audit,
        IOptions<SubscriptionOptions> options,
        TimeProvider clock,
        CancellationToken ct)
    {
        var (tenant, sub) = await LoadAsync(db, user, ct);
        var settings = options.Value;
        var now = clock.GetUtcNow().UtcDateTime;
        var before = TenantSubscriptionEndpoints.Snapshot(sub);

        string action;
        if (sub.Outstanding > 0)
        {
            action = "PaidOutstanding";
        }
        else
        {
            var opensAt = RenewableFrom(sub, settings);
            if (now < opensAt)
            {
                throw new BusinessRuleException(
                    "subscription.renewal_not_open", $"Renewal opens on {opensAt:yyyy-MM-dd}; the current period is paid until {sub.PeriodEndUtc:yyyy-MM-dd}.");
            }

            sub.Renew(now);
            action = "Renewed";
        }

        var amount = sub.Outstanding;
        if (amount <= 0)
        {
            throw new BusinessRuleException("subscription.nothing_to_pay", "This plan has no fee to pay.");
        }

        sub.AddPayment(amount, now);
        var payment = new SubscriptionPayment(sub.Id, tenant.Id, amount, now, SimulatedMethod, $"SIM-{Guid.NewGuid():N}"[..16].ToUpperInvariant(), sub.PeriodStartUtc, user.UserId);
        db.SubscriptionPayments.Add(payment);
        audit.Record(AuditScope.Tenant, tenant.Id, action == "Renewed" ? "subscription.renewed_simulated" : "subscription.paid_simulated", "Subscription", sub.Id, before, TenantSubscriptionEndpoints.Snapshot(sub));
        audit.Record(AuditScope.Platform, tenant.Id, action == "Renewed" ? "subscription.renewed_simulated" : "subscription.paid_simulated", "Subscription", sub.Id, before, TenantSubscriptionEndpoints.Snapshot(sub));
        await db.SaveChangesAsync(ct);

        return new RenewalResult(action, amount, SubscriptionPaymentResponse.From(payment), await BuildAsync(db, tenant, sub, settings, clock, ct));
    }

    /// <summary>The first moment a paid period may be renewed: the start of the notice window before its end.</summary>
    private static DateTime RenewableFrom(TenantSubscription sub, SubscriptionOptions settings) => sub.PeriodEndUtc.AddDays(-settings.RenewalNoticeDays);

    private static async Task<MySubscriptionResponse> BuildAsync(
        IdentityOrganizationDbContext db, Tenant tenant, TenantSubscription sub, SubscriptionOptions settings, TimeProvider clock, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var opensAt = RenewableFrom(sub, settings);
        var canRenew = sub.Outstanding > 0 || now >= opensAt;
        var amount = sub.Outstanding > 0 ? sub.Outstanding : canRenew ? sub.Fee : 0;
        return new MySubscriptionResponse(
            await TenantSubscriptionEndpoints.ToResponseAsync(db, tenant, sub, settings, clock, ct), canRenew && amount > 0, sub.Outstanding > 0 ? null : opensAt, amount);
    }

    private static async Task<(Tenant Tenant, TenantSubscription Subscription)> LoadAsync(IdentityOrganizationDbContext db, ICurrentUser user, CancellationToken ct)
    {
        var tenantId = TenantScope.Require(user);
        var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Id == tenantId, ct) ?? throw new NotFoundException("Tenant", tenantId);
        var sub = await SubscriptionRules.CurrentAsync(db, tenantId, ct)
            ?? throw new NotFoundException("Subscription of your brand", tenantId);
        return (tenant, sub);
    }
}
