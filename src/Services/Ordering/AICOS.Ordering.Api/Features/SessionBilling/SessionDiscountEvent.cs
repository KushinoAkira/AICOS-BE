using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AICOS.Ordering.Api.Features.SessionBilling;

/// <summary>Append-only receipt for replacing a session's manual discount.</summary>
public sealed class SessionDiscountEvent
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid BranchId { get; set; }
    public Guid SessionId { get; set; }
    public Guid ActorId { get; set; }
    public long Revision { get; set; }
    public string RequestHash { get; set; } = "";
    public string Reason { get; set; } = "";
    public string Mode { get; set; } = "";
    public decimal Value { get; set; }
    public string AllocationsJson { get; set; } = "[]";
    public string ResultJson { get; set; } = "{}";
    public DateTime CreatedAtUtc { get; set; }

    internal bool RequiresReview(string fingerprint) => Value > 0 &&
        System.Text.Json.JsonSerializer.Deserialize<SessionDiscountView>(ResultJson)!.Fingerprint != fingerprint;
}

internal sealed class SessionDiscountEventMapping : IEntityTypeConfiguration<SessionDiscountEvent>
{
    public void Configure(EntityTypeBuilder<SessionDiscountEvent> b)
    {
        b.ToTable("session_discount_events", t =>
        {
            t.HasCheckConstraint("ck_session_discount_input", "mode IN ('AMOUNT','PERCENT') AND value >= 0 AND revision > 0");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.RequestHash).HasMaxLength(64).IsRequired();
        b.Property(x => x.Reason).HasMaxLength(250).IsRequired();
        b.Property(x => x.Mode).HasMaxLength(10).IsRequired();
        b.Property(x => x.Value).HasPrecision(18, 2);
        b.Property(x => x.AllocationsJson).HasColumnType("jsonb");
        b.Property(x => x.ResultJson).HasColumnType("jsonb");
        b.HasOne<AICOS.Ordering.Api.Domain.TableSession>().WithMany()
            .HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.TenantId, x.SessionId, x.Revision }).IsUnique();
    }
}
