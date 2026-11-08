using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AICOS.KitchenOperations.Api.Features;

/// <summary>
/// The payment of an order arrived before its ticket. Events travel on separate channels, so a cash payment right after the order can
/// overtake the order: it is kept here and the ticket starts paid once it is created. Both sides run under the same per-order lock.
/// </summary>
public sealed class EarlyPayment
{
    public Guid TenantId { get; set; }
    public Guid OrderId { get; set; }
    public DateTime PaidAtUtc { get; set; }
}

internal sealed class EarlyPaymentConfiguration : IEntityTypeConfiguration<EarlyPayment>
{
    public void Configure(EntityTypeBuilder<EarlyPayment> b)
    {
        b.ToTable("early_payments");
        b.HasKey(x => new { x.TenantId, x.OrderId });
    }
}
