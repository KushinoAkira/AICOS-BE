using AICOS.BuildingBlocks.Domain;

namespace AICOS.CustomerEngagement.Api.Domain;

/// <summary>
/// A paid order of a member at a brand, kept so vouchers that ask for "N orders of at least X" can count them. One row per order;
/// an order cancelled after payment (not a no-show) stops counting.
/// </summary>
public sealed class MemberOrder : AuditableEntity<Guid>
{
    private MemberOrder()
    {
    }

    public MemberOrder(Guid tenantId, Guid customerId, Guid orderId, decimal amount, DateTime paidAtUtc)
    {
        Id = Guid.NewGuid();
        TenantId = tenantId;
        CustomerId = customerId;
        OrderId = orderId;
        Amount = amount;
        PaidAtUtc = paidAtUtc;
    }

    public Guid TenantId { get; private set; }

    public Guid CustomerId { get; private set; }

    public Guid OrderId { get; private set; }

    public decimal Amount { get; private set; }

    public DateTime PaidAtUtc { get; private set; }

    public bool IsCancelled { get; private set; }

    public void Cancel() => IsCancelled = true;
}
