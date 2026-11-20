using AICOS.Contracts.Orders;
using AICOS.KitchenOperations.Api.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AICOS.KitchenOperations.Api.Features.OrderCancellation;

public sealed class KitchenCancellationReceipt
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid BranchId { get; set; }
    public Guid OrderId { get; set; }
    public Guid? TicketId { get; set; }
    public string RequestHash { get; set; } = "";
    public string State { get; set; } = "HELD";
    public string? ReasonCode { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public CancellationAck Ack() => new(Id, TenantId, BranchId, OrderId, RequestHash, State, ReasonCode, TicketId);
}

internal sealed class KitchenCancellationMapping : IEntityTypeConfiguration<KitchenCancellationReceipt>
{
    public void Configure(EntityTypeBuilder<KitchenCancellationReceipt> b)
    {
        b.ToTable("order_cancellation_receipts", t => t.HasCheckConstraint("ck_kitchen_cancellation_state",
            "state IN ('HELD','CANCELLED','ABORTED','REJECTED') AND ((state = 'HELD') = (completed_at_utc IS NULL))"));
        b.HasKey(x => x.Id); b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.RequestHash).HasMaxLength(64); b.Property(x => x.State).HasMaxLength(16); b.Property(x => x.ReasonCode).HasMaxLength(100);
        b.HasOne<KitchenTicket>().WithMany().HasForeignKey(x => x.TicketId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.TenantId, x.OrderId }).IsUnique().HasFilter("state = 'HELD'");
    }
}
