using AICOS.BuildingBlocks.Exceptions;
using AICOS.BuildingBlocks.Security;
using AICOS.IdentityOrganization.Api.Domain;
using AICOS.IdentityOrganization.Api.Infrastructure;
using AICOS.IdentityOrganization.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AICOS.IdentityOrganization.Api.Features.Platform;

public sealed record TenantCounts(int Total, int Active, int Suspended, int WithoutPlan);

public sealed record SubscriptionCounts(int Current, int DueSoon, int GracePeriod, int Overdue, int UnpaidCurrentPeriods, decimal OutstandingAmount);

public sealed record RevenueMonth(string Month, decimal Amount);

public sealed record PlanShare(string PlanName, int Tenants, decimal MonthlyRecurringRevenue);

public sealed record RevenueSummary(decimal ThisMonth, decimal ThisYear, decimal AllTime, IReadOnlyList<RevenueMonth> Last12Months);

/// <summary>UC-09 / UC-10 (the tenant and revenue part): the Super Admin's dashboard numbers. SaaS revenue is never mixed with brand sales (BR-09).</summary>
public sealed record PlatformOverviewResponse(
    DateTime GeneratedAtUtc,
    TenantCounts Tenants,
    SubscriptionCounts Subscriptions,
    RevenueSummary Revenue,
    IReadOnlyList<PlanShare> Plans);

public sealed record RevenueReportResponse(DateTime FromUtc, DateTime ToUtc, decimal Total, IReadOnlyList<RevenueMonth> ByMonth, IReadOnlyList<RevenueByPlan> ByPlan);

public sealed record RevenueByPlan(string PlanName, decimal Amount, int Payments);

/// <summary>
/// UC-10 View SaaS Subscription Revenue and the tenant/subscription status of UC-09. Routes: /api/v1/identity/platform.
/// SuperAdmin only. Numbers come from payments the Super Admin recorded (billing is simulated in the MVP).
/// Service health (containers, errors, latency) is served by the gateway: GET /api/v1/platform/health.
/// </summary>
public static class PlatformEndpoints
{
    private const int MaxReportMonths = 60;

    public static void MapPlatformEndpoints(this RouteGroupBuilder api)
    {
        var platform = api.MapGroup("/platform").WithTags("Platform").RequireAuthorization(AicosPolicies.PlatformAdmin);

        platform.MapGet("/overview", OverviewAsync);
        platform.MapGet("/revenue", RevenueAsync);
    }

    private static async Task<PlatformOverviewResponse> OverviewAsync(
        IdentityOrganizationDbContext db, IOptions<SubscriptionOptions> options, TimeProvider clock, CancellationToken ct)
    {
        var settings = options.Value;
        var now = clock.GetUtcNow().UtcDateTime;

        var tenantStatuses = await db.Tenants.AsNoTracking().GroupBy(t => t.Status).Select(g => new { Status = g.Key, Count = g.Count() }).ToListAsync(ct);
        var subs = await db.TenantSubscriptions.AsNoTracking().Where(s => s.Status == SubscriptionStatus.Active).ToListAsync(ct);

        var health = subs.Select(s => s.HealthAt(now, settings.RenewalNoticeDays, settings.GracePeriodDays)).ToList();
        var unpaid = subs.Where(s => s.PaymentStatus == SubscriptionPaymentStatus.Unpaid).ToList();

        var tenants = new TenantCounts(
            tenantStatuses.Sum(x => x.Count),
            tenantStatuses.Where(x => x.Status == TenantStatus.Active).Sum(x => x.Count),
            tenantStatuses.Where(x => x.Status == TenantStatus.Suspended).Sum(x => x.Count),
            tenantStatuses.Sum(x => x.Count) - subs.Count);

        var subscriptions = new SubscriptionCounts(
            health.Count(h => h == SubscriptionHealth.Current),
            health.Count(h => h == SubscriptionHealth.DueSoon),
            health.Count(h => h == SubscriptionHealth.GracePeriod),
            health.Count(h => h == SubscriptionHealth.Overdue),
            unpaid.Count,
            unpaid.Sum(s => s.Outstanding));

        var plans = subs.GroupBy(s => s.PlanName)
            .Select(g => new PlanShare(g.Key, g.Count(), g.Sum(s => s.Fee / (int)s.BillingPeriod)))
            .OrderByDescending(p => p.Tenants).ThenBy(p => p.PlanName).ToList();

        var monthStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var yearStart = new DateTime(now.Year, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var seriesStart = monthStart.AddMonths(-11);

        var payments = await db.SubscriptionPayments.AsNoTracking().Select(p => new { p.Amount, p.PaidAtUtc }).ToListAsync(ct);
        var series = BuildSeries(payments.Where(p => p.PaidAtUtc >= seriesStart).Select(p => (p.PaidAtUtc, p.Amount)), seriesStart, monthStart);

        var revenue = new RevenueSummary(
            payments.Where(p => p.PaidAtUtc >= monthStart).Sum(p => p.Amount),
            payments.Where(p => p.PaidAtUtc >= yearStart).Sum(p => p.Amount),
            payments.Sum(p => p.Amount),
            series);

        return new PlatformOverviewResponse(now, tenants, subscriptions, revenue, plans);
    }

    /// <summary>Revenue between two dates (default: the last 12 months), by month and by plan. At most 60 months.</summary>
    private static async Task<RevenueReportResponse> RevenueAsync(
        IdentityOrganizationDbContext db, TimeProvider clock, DateTime? from, DateTime? to, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var end = (to ?? now).ToUniversalTime();
        var start = (from ?? new DateTime(end.Year, end.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(-11)).ToUniversalTime();

        if (start > end)
        {
            throw new ValidationFailedException(nameof(from), "'from' must not be later than 'to'.");
        }

        if ((end.Year - start.Year) * 12 + end.Month - start.Month >= MaxReportMonths)
        {
            throw new ValidationFailedException(nameof(from), $"The report covers at most {MaxReportMonths} months.");
        }

        var rows = await (from p in db.SubscriptionPayments.AsNoTracking()
                          join s in db.TenantSubscriptions.AsNoTracking() on p.SubscriptionId equals s.Id
                          where p.PaidAtUtc >= start && p.PaidAtUtc <= end
                          select new { p.Amount, p.PaidAtUtc, s.PlanName }).ToListAsync(ct);

        var firstMonth = new DateTime(start.Year, start.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var lastMonth = new DateTime(end.Year, end.Month, 1, 0, 0, 0, DateTimeKind.Utc);

        return new RevenueReportResponse(
            start,
            end,
            rows.Sum(r => r.Amount),
            BuildSeries(rows.Select(r => (r.PaidAtUtc, r.Amount)), firstMonth, lastMonth),
            rows.GroupBy(r => r.PlanName).Select(g => new RevenueByPlan(g.Key, g.Sum(r => r.Amount), g.Count())).OrderByDescending(p => p.Amount).ToList());
    }

    /// <summary>One entry per month from <paramref name="first"/> to <paramref name="last"/>, zero where nothing was received.</summary>
    private static List<RevenueMonth> BuildSeries(IEnumerable<(DateTime PaidAtUtc, decimal Amount)> payments, DateTime first, DateTime last)
    {
        var byMonth = payments.GroupBy(p => (p.PaidAtUtc.Year, p.PaidAtUtc.Month)).ToDictionary(g => g.Key, g => g.Sum(p => p.Amount));
        var result = new List<RevenueMonth>();
        for (var month = first; month <= last; month = month.AddMonths(1))
        {
            result.Add(new RevenueMonth($"{month:yyyy-MM}", byMonth.GetValueOrDefault((month.Year, month.Month))));
        }

        return result;
    }
}
