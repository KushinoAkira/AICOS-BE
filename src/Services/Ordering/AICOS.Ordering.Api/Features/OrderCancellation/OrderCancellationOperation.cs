using System.Text.Json;
using AICOS.Contracts.Orders;
using AICOS.Ordering.Api.Domain;
using AICOS.Ordering.Api.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AICOS.Ordering.Api.Features.OrderCancellation;

public sealed class OrderCancellationOperation
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid BranchId { get; set; }
    public Guid SessionId { get; set; }
    public Guid TableId { get; set; }
    public Guid OrderId { get; set; }
    public Guid ActorId { get; set; }
    public string RequestHash { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public string OrderCode { get; set; } = "";
    public string TableCode { get; set; } = "";
    public string Reason { get; set; } = "";
    public string State { get; set; } = "QUEUED";
    public string? ReasonCode { get; set; }
    public string BeforeJson { get; set; } = "{}";
    public string? AfterJson { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime NextAttemptAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public DateTime? LegacyPublishedAtUtc { get; set; }
    public int Attempts { get; set; }
    public long Version { get; set; }

    public CancellationReservation Reservation() => new(Id, TenantId, BranchId, OrderId, ActorId, OrderCode, TableCode, Fingerprint);
    public void Finish(string state, string? code, DateTime now)
    { State = state; ReasonCode = code; CompletedAtUtc = now; Version++; }
}

internal sealed class OrderCancellationMapping : IEntityTypeConfiguration<OrderCancellationOperation>
{
    public void Configure(EntityTypeBuilder<OrderCancellationOperation> b)
    {
        b.ToTable("order_cancellation_operations", t => t.HasCheckConstraint("ck_order_cancellation_state",
            "state IN ('QUEUED','RESERVED','APPLIED','ABORTING','COMPLETED','REJECTED','ABORTED') AND attempts >= 0 AND version >= 0 AND ((state IN ('COMPLETED','REJECTED','ABORTED')) = (completed_at_utc IS NOT NULL))"));
        b.HasKey(x => x.Id); b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.RequestHash).HasMaxLength(64); b.Property(x => x.Fingerprint).HasMaxLength(64);
        b.Property(x => x.OrderCode).HasMaxLength(40); b.Property(x => x.TableCode).HasMaxLength(20);
        b.Property(x => x.Reason).HasMaxLength(250); b.Property(x => x.State).HasMaxLength(16); b.Property(x => x.ReasonCode).HasMaxLength(100);
        b.Property(x => x.BeforeJson).HasColumnType("jsonb"); b.Property(x => x.AfterJson).HasColumnType("jsonb");
        b.HasOne<Order>().WithMany().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<TableSession>().WithMany().HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.TenantId, x.OrderId }).IsUnique().HasFilter("completed_at_utc IS NULL");
        b.HasIndex(x => new { x.TenantId, x.SessionId });
        b.HasIndex(x => x.NextAttemptAtUtc).HasFilter("completed_at_utc IS NULL");
    }
}
