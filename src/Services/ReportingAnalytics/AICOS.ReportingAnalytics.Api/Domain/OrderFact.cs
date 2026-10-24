using AICOS.BuildingBlocks.Domain;

namespace AICOS.ReportingAnalytics.Api.Domain;

/// <summary>
/// One row per order, built from the events of the Order and Payment services (the reports never read the database of another service).
/// Events can arrive in any order, so every field is filled when its event arrives and the row is created by whichever comes first.
/// </summary>
public sealed class OrderFact : AuditableEntity<Guid>, IBranchScoped
{
    public Guid TenantId { get; private set; }

    public Guid BranchId { get; private set; }

    /// <summary>DineIn, Takeaway or Delivery; empty until the order created event arrives.</summary>
    public string OrderType { get; private set; } = string.Empty;

    public decimal TotalAmount { get; private set; }

    /// <summary>When the order was placed (the time of the event, not of its arrival).</summary>
    public DateTime? PlacedAtUtc { get; private set; }

    public DateTime? PaidAtUtc { get; private set; }

    /// <summary>The provider that took the money: Cash, Mock, VietQR, Momo, ZaloPay.</summary>
    public string? PaymentMethod { get; private set; }

    public decimal PaidAmount { get; private set; }

    public decimal RefundedAmount { get; private set; }

    public DateTime? CancelledAtUtc { get; private set; }

    public DateTime? CompletedAtUtc { get; private set; }

    private OrderFact()
    {
    }

    public OrderFact(Guid orderId, Guid tenantId, Guid branchId)
    {
        Id = orderId;
        TenantId = tenantId;
        BranchId = branchId;
    }

    public bool IsPaid => PaidAtUtc is not null;

    /// <summary>An order that was cancelled and never paid never counts as an order.</summary>
    public bool Counts => !(CancelledAtUtc is not null && PaidAtUtc is null);

    public void Placed(string orderType, decimal total, DateTime placedAtUtc)
    {
        OrderType = orderType;
        TotalAmount = total;
        PlacedAtUtc = placedAtUtc;
    }

    public void Paid(string method, decimal amount, DateTime paidAtUtc)
    {
        PaymentMethod = method;
        PaidAmount = amount;
        PaidAtUtc = paidAtUtc;
        if (TotalAmount == 0)
        {
            TotalAmount = amount;
        }
    }

    public void Refunded(decimal totalRefunded) => RefundedAmount = Math.Max(RefundedAmount, totalRefunded);

    public void Cancelled(DateTime at) => CancelledAtUtc ??= at;

    public void Completed(DateTime at) => CompletedAtUtc ??= at;
}
