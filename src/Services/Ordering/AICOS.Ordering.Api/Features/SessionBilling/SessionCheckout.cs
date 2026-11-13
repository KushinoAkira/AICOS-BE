using AICOS.Contracts.Billing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
namespace AICOS.Ordering.Api.Features.SessionBilling;

/// <summary>Durable no-timeout financial fence and one-payment inbox. Never released by a timer.</summary>
public sealed class SessionCheckout
{
    public Guid SessionId { get; set; }
    public Guid TenantId { get; set; }
    public Guid BranchId { get; set; }
    public Guid BillId { get; set; }
    public string Fingerprint { get; set; } = "";
    public string QuoteJson { get; set; } = "";
    public string State { get; set; } = "Frozen";
    public Guid? PaymentId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}
internal sealed class SessionCheckoutMapping : IEntityTypeConfiguration<SessionCheckout>
{
    public void Configure(EntityTypeBuilder<SessionCheckout> b)
    {
        b.ToTable("session_checkouts", t => t.HasCheckConstraint("ck_session_checkout_state", "state IN ('Frozen','Paid','Closed')"));
        b.HasKey(x => x.SessionId); b.Property(x => x.SessionId).ValueGeneratedNever();
        b.Property(x => x.Fingerprint).HasMaxLength(64); b.Property(x => x.State).HasMaxLength(16);
        b.Property(x => x.QuoteJson).HasColumnType("jsonb");
        b.HasIndex(x => new { x.TenantId, x.BillId }).IsUnique();
    }
}
