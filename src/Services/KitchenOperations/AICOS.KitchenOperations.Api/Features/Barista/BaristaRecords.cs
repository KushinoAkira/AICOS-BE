using AICOS.KitchenOperations.Api.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
namespace AICOS.KitchenOperations.Api.Features.Barista;

public sealed class BaristaAcceptance
{
    public Guid TicketId
    {
        get; set;
    }
    public int Cycle
    {
        get; set;
    }
    public Guid TenantId
    {
        get; set;
    }
    public Guid BranchId
    {
        get; set;
    }
    public Guid AcceptedBy
    {
        get; set;
    }
    public string KeyHash { get; set; } = "";
    public string AcceptedName { get; set; } = "";
    public DateTime AcceptedAtUtc
    {
        get; set;
    }
    public string RequestHash { get; set; } = "";
    public DateTime? CompletedAtUtc
    {
        get; set;
    }
    public Guid? CompletedBy
    {
        get; set;
    }
}
public sealed class KitchenPrintJob
{
    public Guid Id
    {
        get; set;
    }
    public Guid TenantId
    {
        get; set;
    }
    public Guid BranchId
    {
        get; set;
    }
    public Guid TicketId
    {
        get; set;
    }
    public int Cycle
    {
        get; set;
    }
    public Guid RequestedBy
    {
        get; set;
    }
    public Guid? ReprintOf
    {
        get; set;
    }
    public string KeyHash { get; set; } = "";
    public string RequestHash { get; set; } = "";
    public string Kind { get; set; } = "SLIP";
    public string Status { get; set; } = "QUEUED";
    public string Payload { get; set; } = "";
    public string PayloadHash { get; set; } = "";
    public string? TableSnapshot
    {
        get; set;
    }
    public DateTime CreatedAtUtc
    {
        get; set;
    }
    public DateTime? StartedAtUtc
    {
        get; set;
    }
    public DateTime? PrintedAtUtc
    {
        get; set;
    }
    public bool TestOutput
    {
        get; set;
    }
    public string? ErrorCode
    {
        get; set;
    }
    public Guid? LeaseOwner
    {
        get; set;
    }
    public DateTime? LeaseUntilUtc
    {
        get; set;
    }
    public long Fence
    {
        get; set;
    }
}
internal sealed class AcceptanceMapping : IEntityTypeConfiguration<BaristaAcceptance>
{
    public void Configure(EntityTypeBuilder<BaristaAcceptance> b)
    {
        b.ToTable("barista_acceptances", t => t.HasCheckConstraint("ck_barista_cycle", "cycle >= 0 AND (completed_at_utc IS NULL OR completed_at_utc >= accepted_at_utc)"));
        b.HasKey(x => new { x.TicketId, x.Cycle });
        b.Property(x => x.AcceptedName).HasMaxLength(255);
        b.Property(x => x.RequestHash).HasMaxLength(64);
        b.Property(x => x.KeyHash).HasMaxLength(64);
        b.HasIndex(x => new { x.TenantId, x.AcceptedBy, x.KeyHash }).IsUnique();
        b.HasIndex(x => new { x.TenantId, x.BranchId, x.AcceptedAtUtc });
        b.HasOne<KitchenTicket>().WithMany().HasForeignKey(x => x.TicketId).OnDelete(DeleteBehavior.Restrict);
    }
}
internal sealed class PrintJobMapping : IEntityTypeConfiguration<KitchenPrintJob>
{
    public void Configure(EntityTypeBuilder<KitchenPrintJob> b)
    {
        b.ToTable("kitchen_print_jobs", t =>
        {
            t.HasCheckConstraint("ck_kitchen_print_state", "status IN ('QUEUED','PRINTING','PRINTED','FAILED','UNKNOWN') AND fence >= 0");
            t.HasCheckConstraint("ck_kitchen_print_kind", "kind IN ('SLIP','LABELS')");
            t.HasCheckConstraint("ck_kitchen_print_ack", "(status = 'PRINTED' AND printed_at_utc IS NOT NULL) OR (status <> 'PRINTED' AND printed_at_utc IS NULL)");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Payload).HasColumnType("jsonb");
        b.Property(x => x.Kind).HasMaxLength(16);
        b.Property(x => x.Status).HasMaxLength(16);
        b.Property(x => x.KeyHash).HasMaxLength(64);
        b.Property(x => x.RequestHash).HasMaxLength(64);
        b.Property(x => x.PayloadHash).HasMaxLength(64);
        b.Property(x => x.TableSnapshot).HasMaxLength(20);
        b.Property(x => x.ErrorCode).HasMaxLength(80);
        b.HasIndex(x => new { x.TenantId, x.RequestedBy, x.KeyHash }).IsUnique();
        b.HasIndex(x => new { x.TicketId, x.Cycle }).IsUnique().HasFilter("kind = 'SLIP' AND reprint_of IS NULL");
        b.HasIndex(x => new { x.Status, x.CreatedAtUtc });
        b.HasOne<KitchenTicket>().WithMany().HasForeignKey(x => x.TicketId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<KitchenPrintJob>().WithMany().HasForeignKey(x => x.ReprintOf).OnDelete(DeleteBehavior.Restrict);
    }
}
