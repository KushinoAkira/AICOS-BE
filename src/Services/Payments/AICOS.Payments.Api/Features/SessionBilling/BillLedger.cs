using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
namespace AICOS.Payments.Api.Features.SessionBilling;

public sealed class SessionBill
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid BranchId { get; set; }
    public Guid SessionId { get; set; }
    public string Fingerprint { get; set; } = "";
    public string QuoteJson { get; set; } = "";
    public string Status { get; set; } = "UNPAID";
    public long Version { get; set; } = 1;
    public decimal Total { get; set; }
    public Guid CreatedByUserId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public Guid? PaidPaymentId { get; set; }
    public DateTime? PaidAtUtc { get; set; }
    public DateTime? ClosedAtUtc { get; set; }
    public bool OrderingAcknowledged { get; set; }
    public bool NeedsReconciliation { get; set; }
}
public sealed class SessionBillOrder
{
    public Guid BillId { get; set; }
    public Guid OrderId { get; set; }
    public decimal Amount { get; set; }
}
public sealed class BillAttempt
{
    public Guid Id { get; set; }
    public Guid BillId { get; set; }
    public Guid TenantId { get; set; }
    public Guid CreatedByUserId { get; set; }
    public string KeyHash { get; set; } = "";
    public string RequestHash { get; set; } = "";
    public string Method { get; set; } = "";
    public string? Provider { get; set; }
    public string Status { get; set; } = "PENDING";
    public string TransactionRef { get; set; } = "";
    public string? ProviderReference { get; set; }
    public string? CheckoutUrl { get; set; }
    public decimal Amount { get; set; }
    public decimal? Tendered { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? PaidAtUtc { get; set; }
}
/// <summary>Append-only financial journal; PAID events double as durable retryable synchronization outbox.</summary>
public sealed class BillJournal
{
    public Guid Id { get; set; }
    public Guid BillId { get; set; }
    public string Kind { get; set; } = "";
    public string Payload { get; set; } = "{}";
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? DeliveredAtUtc { get; set; }
    public DateTime NextAttemptAtUtc { get; set; }
    public Guid? LeaseOwner { get; set; }
    public DateTime? LeaseUntilUtc { get; set; }
    public long Fence { get; set; }
}
internal sealed class BillMapping : IEntityTypeConfiguration<SessionBill>
{
    public void Configure(EntityTypeBuilder<SessionBill> b)
    {
        b.ToTable("session_bills", t => { t.HasCheckConstraint("ck_session_bill_total", "total >= 0"); t.HasCheckConstraint("ck_session_bill_status", "status IN ('UNPAID','PAID','CLOSED')"); });
        b.HasKey(x => x.Id); b.Property(x => x.Id).ValueGeneratedNever(); b.Property(x => x.Total).HasPrecision(18,0);
        b.Property(x => x.Fingerprint).HasMaxLength(64); b.Property(x => x.Status).HasMaxLength(16);
        b.Property(x => x.QuoteJson).HasColumnType("jsonb"); b.HasIndex(x => new { x.TenantId, x.SessionId }).IsUnique();
    }
}
internal sealed class BillOrderMapping : IEntityTypeConfiguration<SessionBillOrder>
{
    public void Configure(EntityTypeBuilder<SessionBillOrder> b)
    {
        b.ToTable("session_bill_orders"); b.HasKey(x => new { x.BillId, x.OrderId }); b.Property(x => x.Amount).HasPrecision(18,0);
        b.HasOne<SessionBill>().WithMany().HasForeignKey(x => x.BillId).OnDelete(DeleteBehavior.Restrict);
    }
}
internal sealed class BillAttemptMapping : IEntityTypeConfiguration<BillAttempt>
{
    public void Configure(EntityTypeBuilder<BillAttempt> b)
    {
        b.ToTable("session_bill_attempts", t => { t.HasCheckConstraint("ck_bill_attempt_amount", "amount > 0"); t.HasCheckConstraint("ck_bill_attempt_status", "status IN ('PENDING','SUCCEEDED','FAILED')"); });
        b.HasKey(x => x.Id); b.Property(x => x.Id).ValueGeneratedNever(); b.Property(x => x.Amount).HasPrecision(18,0); b.Property(x => x.Tendered).HasPrecision(18,0);
        b.Property(x => x.KeyHash).HasMaxLength(64); b.Property(x => x.RequestHash).HasMaxLength(64);
        b.Property(x => x.TransactionRef).HasMaxLength(64); b.Property(x => x.Status).HasMaxLength(16); b.Property(x => x.Method).HasMaxLength(16);
        b.Property(x => x.Provider).HasMaxLength(32); b.Property(x => x.ProviderReference).HasMaxLength(128);
        b.HasIndex(x => new { x.TenantId, x.KeyHash }).IsUnique(); b.HasIndex(x => x.TransactionRef).IsUnique();
        b.HasIndex(x => x.BillId).IsUnique().HasFilter("status = 'PENDING'").HasDatabaseName("uq_bill_pending_attempt");
        b.HasOne<SessionBill>().WithMany().HasForeignKey(x => x.BillId).OnDelete(DeleteBehavior.Restrict);
    }
}
internal sealed class BillJournalMapping : IEntityTypeConfiguration<BillJournal>
{
    public void Configure(EntityTypeBuilder<BillJournal> b)
    {
        b.ToTable("session_bill_events"); b.HasKey(x => x.Id); b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Kind).HasMaxLength(32); b.Property(x => x.Payload).HasColumnType("jsonb");
        b.HasIndex(x => new { x.NextAttemptAtUtc, x.Id }).HasFilter("kind = 'PAID' AND delivered_at_utc IS NULL");
        b.HasOne<SessionBill>().WithMany().HasForeignKey(x => x.BillId).OnDelete(DeleteBehavior.Restrict);
    }
}
