using AICOS.BuildingBlocks.Exceptions;
using AICOS.BuildingBlocks.Security;
using AICOS.BuildingBlocks.Validation;
using AICOS.Payments.Api.Domain;
using AICOS.Payments.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AICOS.Payments.Api.Features.Payments;

public sealed record Discrepancy(string Code, string Message, Guid? PaymentId, Guid? OrderId, decimal? Amount);

public sealed record MethodTotals(string Method, int Count, decimal Gross, decimal Refunded, decimal Net);

public sealed record ReconciliationReport(
    DateOnly From,
    DateOnly To,
    Guid? BranchId,
    int PaymentCount,
    int PaidCount,
    decimal Gross,
    decimal Refunded,
    decimal Net,
    int RefundsPendingApproval,
    IReadOnlyList<MethodTotals> ByMethod,
    IReadOnlyDictionary<string, int> ByStatus,
    IReadOnlyList<Discrepancy> Discrepancies);

/// <summary>An order as the Order service sees it, to compare with what was paid.</summary>
public sealed record OrderForReconciliation(Guid? OrderId, decimal? Amount, bool? IsPaid);

public sealed record OrderReconciliationRequest(Guid? BranchId, DateOnly? From, DateOnly? To, List<OrderForReconciliation>? Orders);

public sealed record OrderReconciliationReport(int Orders, int Matched, IReadOnlyList<Discrepancy> Discrepancies);

/// <summary>
/// Reconciling electronic transactions with orders (SCRUM-89). The first report works from the Payment service alone: totals per method
/// and the payments that need attention. The second compares the payments with a list of orders (which the Order service sends once it
/// exists) and names every order and payment that disagree. Routes: /api/v1/payments/reconciliation. BrandAdmin and BranchManager.
/// </summary>
public static class ReconciliationEndpoints
{
    private const int MaxOrders = 5000;
    private const int MaxRangeDays = 93;

    public static void MapReconciliationEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/reconciliation").WithTags("Reconciliation").RequireAuthorization(AicosPolicies.Management);

        group.MapGet("/", ReportAsync);
        group.MapPost("/orders", CompareOrdersAsync);
    }

    private static async Task<ReconciliationReport> ReportAsync(
        PaymentsDbContext db, ICurrentUser user, IOptions<Infrastructure.PaymentOptions> options, TimeProvider clock, Guid? branchId, DateOnly? from, DateOnly? to, CancellationToken ct)
    {
        var tenantId = TenantScope.Require(user);
        var branch = BranchAccess.ResolveFilter(user, branchId);
        var (fromDate, toDate, startUtc, endUtc) = Range(options.Value, clock, from, to);
        var now = clock.GetUtcNow().UtcDateTime;

        var payments = await InRange(db, tenantId, branch, startUtc, endUtc).AsNoTracking().ToListAsync(ct);
        var paid = payments.Where(p => p.IsPaid).ToList();
        var byMethod = paid.GroupBy(p => p.Method).OrderBy(g => g.Key)
            .Select(g => new MethodTotals(g.Key, g.Count(), g.Sum(p => p.Amount), g.Sum(p => p.RefundedAmount), g.Sum(p => p.Amount - p.RefundedAmount)))
            .ToList();

        var discrepancies = new List<Discrepancy>();
        foreach (var p in payments)
        {
            if (p.Status == PaymentStatus.Pending && p.ExpiresAtUtc <= now)
            {
                discrepancies.Add(new Discrepancy("stale_pending", "Still pending after its time ran out; the expiry job has not run or the order was not released.", p.Id, p.OrderId, p.Amount));
            }

            if (p.ProviderState == ProviderState.Succeeded && !p.IsPaid)
            {
                discrepancies.Add(new Discrepancy("provider_paid_not_applied", "The provider says it was paid but we never recorded it (lost webhook). Run the status query.", p.Id, p.OrderId, p.Amount));
            }

            if (p.PaidLate)
            {
                discrepancies.Add(new Discrepancy("paid_late", "Money arrived after the payment expired or was cancelled.", p.Id, p.OrderId, p.Amount));
            }
        }

        foreach (var group in paid.GroupBy(p => p.OrderId).Where(g => g.Count() > 1))
        {
            foreach (var p in group.OrderBy(p => p.PaidAtUtc).Skip(1))
            {
                discrepancies.Add(new Discrepancy("duplicate_payment", "The order was paid more than once; refund the extra payment.", p.Id, p.OrderId, p.Amount));
            }
        }

        var mismatches = await db.WebhookEvents.AsNoTracking()
            .Where(e => e.TenantId == tenantId && e.Outcome == "amount_mismatch" && e.ReceivedAtUtc >= startUtc && e.ReceivedAtUtc < endUtc)
            .ToListAsync(ct);
        foreach (var e in mismatches)
        {
            discrepancies.Add(new Discrepancy("webhook_amount_mismatch", $"{e.ProviderCode} reported {e.ReportedAmount:0}, which differs from the payment amount; not applied.", e.PaymentId, null, e.ReportedAmount));
        }

        var pendingRefunds = await db.PaymentRefunds.CountAsync(
            r => r.TenantId == tenantId && r.Status == RefundStatus.PendingApproval && (branch == null || r.BranchId == branch), ct);

        return new ReconciliationReport(
            fromDate, toDate, branch, payments.Count, paid.Count, paid.Sum(p => p.Amount), paid.Sum(p => p.RefundedAmount), paid.Sum(p => p.Amount - p.RefundedAmount),
            pendingRefunds, byMethod, payments.GroupBy(p => p.Status.ToString()).ToDictionary(g => g.Key, g => g.Count()), discrepancies);
    }

    private static async Task<OrderReconciliationReport> CompareOrdersAsync(
        OrderReconciliationRequest r, PaymentsDbContext db, ICurrentUser user, IOptions<Infrastructure.PaymentOptions> options, TimeProvider clock, CancellationToken ct)
    {
        var errors = new ValidationErrors();
        if (r.Orders is null || r.Orders.Count == 0)
        {
            errors.Add("Orders", "Give at least one order.");
        }
        else if (r.Orders.Count > MaxOrders)
        {
            errors.Add("Orders", $"At most {MaxOrders} orders per call.");
        }
        else if (r.Orders.Any(o => o.OrderId is null || o.Amount is null || o.IsPaid is null))
        {
            errors.Add("Orders", "Every order needs orderId, amount and isPaid.");
        }
        else if (r.Orders.GroupBy(o => o.OrderId).Any(g => g.Count() > 1))
        {
            errors.Add("Orders", "An order appears twice.");
        }

        errors.ThrowIfAny();

        var tenantId = TenantScope.Require(user);
        var branch = BranchAccess.ResolveFilter(user, r.BranchId);
        var (_, _, startUtc, endUtc) = Range(options.Value, clock, r.From, r.To);

        var ids = r.Orders!.Select(o => o.OrderId!.Value).ToList();
        var payments = await db.Payments.AsNoTracking()
            .Where(p => p.TenantId == tenantId && (branch == null || p.BranchId == branch)
                && ((p.CreatedAtUtc >= startUtc && p.CreatedAtUtc < endUtc) || ids.Contains(p.OrderId)))
            .ToListAsync(ct);
        var paidByOrder = payments.Where(p => p.IsPaid).GroupBy(p => p.OrderId).ToDictionary(g => g.Key, g => g.ToList());

        var discrepancies = new List<Discrepancy>();
        var matched = 0;
        foreach (var order in r.Orders!)
        {
            var orderId = order.OrderId!.Value;
            var paid = paidByOrder.GetValueOrDefault(orderId);
            var paidTotal = paid?.Sum(p => p.Amount) ?? 0;
            if (order.IsPaid == true && paid is null)
            {
                discrepancies.Add(new Discrepancy("order_paid_without_payment", "The order says paid but no successful payment exists.", null, orderId, order.Amount));
            }
            else if (order.IsPaid != true && paid is not null)
            {
                discrepancies.Add(new Discrepancy("payment_without_paid_order", "Money was received but the order is not marked paid.", paid[0].Id, orderId, paidTotal));
            }
            else if (order.IsPaid == true && paidTotal != order.Amount)
            {
                discrepancies.Add(new Discrepancy("amount_mismatch", $"The order is {order.Amount:0} but {paidTotal:0} was paid.", paid![0].Id, orderId, paidTotal));
            }
            else
            {
                matched++;
            }
        }

        var known = ids.ToHashSet();
        foreach (var p in payments.Where(p => p.IsPaid && !known.Contains(p.OrderId) && p.CreatedAtUtc >= startUtc && p.CreatedAtUtc < endUtc))
        {
            discrepancies.Add(new Discrepancy("payment_without_order", "A paid payment whose order was not in the list.", p.Id, p.OrderId, p.Amount));
        }

        return new OrderReconciliationReport(r.Orders.Count, matched, discrepancies);
    }

    private static IQueryable<Payment> InRange(PaymentsDbContext db, Guid tenantId, Guid? branch, DateTime startUtc, DateTime endUtc) =>
        db.Payments.Where(p => p.TenantId == tenantId && (branch == null || p.BranchId == branch) && p.CreatedAtUtc >= startUtc && p.CreatedAtUtc < endUtc);

    /// <summary>Inclusive shop-day range (default today) turned into a UTC interval.</summary>
    private static (DateOnly From, DateOnly To, DateTime StartUtc, DateTime EndUtc) Range(Infrastructure.PaymentOptions options, TimeProvider clock, DateOnly? from, DateOnly? to)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(options.TimeZone);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(clock.GetUtcNow().UtcDateTime, zone));
        var start = from ?? to ?? today;
        var end = to ?? from ?? today;
        if (end < start)
        {
            throw new ValidationFailedException("to", "The end date is before the start date.");
        }

        if (end.DayNumber - start.DayNumber >= MaxRangeDays)
        {
            throw new ValidationFailedException("to", $"The range can cover at most {MaxRangeDays} days.");
        }

        return (start, end,
            TimeZoneInfo.ConvertTimeToUtc(start.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified), zone),
            TimeZoneInfo.ConvertTimeToUtc(end.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified), zone));
    }
}
