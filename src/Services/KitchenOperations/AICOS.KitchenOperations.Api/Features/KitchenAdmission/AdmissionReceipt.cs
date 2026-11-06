using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using AICOS.KitchenOperations.Api.Domain.Entities;

namespace AICOS.KitchenOperations.Api.Features.KitchenAdmission;

public sealed class AdmissionReceipt
{
    public string ProducerKey { get; set; } = "ordering";
    public string ConsumerKey { get; set; } = "";
    public Guid EventId { get; set; }
    public Guid TenantId { get; set; }
    public Guid BranchId { get; set; }
    public Guid OrderId { get; set; }
    public byte[] CreationSha256 { get; set; } = [];
    public Guid TicketId { get; set; }
    public string ReceiptStatus { get; set; } = "";
    public DateTime RecordedAtUtc { get; set; }
    public DateTime? AppliedAtUtc { get; set; }
    public DateTime LastVerifiedAtUtc { get; set; }
    public string? LastErrorCode { get; set; }
}

internal sealed class AdmissionReceiptConfiguration : IEntityTypeConfiguration<AdmissionReceipt>
{
    public void Configure(EntityTypeBuilder<AdmissionReceipt> b)
    {
        b.ToTable("order_admission_receipts", t =>
        {
            t.HasCheckConstraint("ck_kitchen_admission_receipt_applied", "(receipt_status = 'APPLIED' AND applied_at_utc IS NOT NULL) OR (receipt_status = 'NEEDS_REVIEW' AND applied_at_utc IS NULL)");
            t.HasCheckConstraint("ck_kitchen_admission_receipt_hash", "octet_length(creation_sha256) = 32");
        });
        b.HasKey(x => new { x.ProducerKey, x.ConsumerKey, x.EventId });
        b.Property(x => x.ProducerKey).HasMaxLength(80); b.Property(x => x.ConsumerKey).HasMaxLength(80);
        b.Property(x => x.ReceiptStatus).HasMaxLength(20); b.Property(x => x.LastErrorCode).HasMaxLength(100);
        b.HasOne<KitchenTicket>().WithMany().HasForeignKey(x => x.TicketId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.TenantId, x.OrderId, x.ConsumerKey }).HasDatabaseName("ix_kitchen_admission_receipt_order");
        b.HasIndex(x => new { x.TenantId, x.BranchId, x.ReceiptStatus }).HasDatabaseName("ix_kitchen_admission_receipt_scope");
    }
}
