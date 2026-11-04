using AICOS.BuildingBlocks.Security;
using AICOS.Payments.Api.Domain;
using AICOS.Payments.Api.Infrastructure;
using AICOS.Payments.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AICOS.Payments.Api.Features.Payments;

/// <summary>A read-only reconciliation snapshot, never a permit to close a session or take more money.</summary>
public sealed record OrderSettlementResponse(
    Guid OrderId, Guid BranchId, string OrderStatus, string OrderingPaymentStatus,
    string State, bool IsSettled, decimal OrderAmount, decimal NetReceived,
    decimal OutstandingAmount, bool HasPendingPayments, bool HasUnresolvedRefunds,
    bool AdvisoryOnly = true);

public static class OrderSettlementEndpoints
{
    public static void MapOrderSettlementEndpoints(this RouteGroupBuilder api) =>
        api.MapGet("/orders/{orderId:guid}/settlement", GetAsync)
            .WithTags("Payments").RequireAuthorization(AicosPolicies.Staff);

    private static async Task<OrderSettlementResponse> GetAsync(
        Guid orderId, PaymentsDbContext db, ICurrentUser user, IOrderDirectory orders, CancellationToken ct)
    {
        var tenant = TenantScope.Require(user);
        // Forwarded caller JWT lets Ordering enforce tenant/branch access; no cross-service DB reads.
        var order = await orders.GetAsync(orderId, ct);
        BranchAccess.EnsureCanAccess(user, order.BranchId);
        var payments = await db.Payments.AsNoTracking()
            .Where(p => p.TenantId == tenant && p.OrderId == orderId).ToListAsync(ct);
        var ids = payments.Select(p => p.Id).ToList();
        var unresolvedRefunds = await db.PaymentRefunds.AsNoTracking()
            .AnyAsync(r => r.TenantId == tenant && ids.Contains(r.PaymentId)
                && (r.Status == RefundStatus.PendingApproval || r.Status == RefundStatus.Failed), ct);
        var paid = payments.Where(p => p.IsPaid).ToList();
        var net = paid.Sum(p => p.Amount - p.RefundedAmount);
        var pending = payments.Any(p => p.Status == PaymentStatus.Pending);
        var voided = order.Status is "Cancelled" or "NoShow" or "Unfulfillable";
        var required = voided ? 0 : order.FinalAmount;
        var inconsistent = paid.Count > 1 || net < 0 || net > required
            || payments.Any(p => p.BranchId != order.BranchId)
            || (!voided && paid.Any(p => p.RefundedAmount > 0));
        var state = inconsistent || unresolvedRefunds ? "ReconciliationRequired"
            : pending ? "Pending"
            : voided && net == 0 ? "Settled"
            : net != required || paid.Count == 0 ? "AwaitingPayment"
            : order.PaymentStatus != "Paid" ? "AwaitingOrderSync"
            : "Settled";
        // Do not expire attempts, query providers, publish events or save changes on this GET.
        return new(orderId, order.BranchId, order.Status, order.PaymentStatus, state,
            state == "Settled", order.FinalAmount, net, Math.Max(0, required - net), pending, unresolvedRefunds);
    }
}
