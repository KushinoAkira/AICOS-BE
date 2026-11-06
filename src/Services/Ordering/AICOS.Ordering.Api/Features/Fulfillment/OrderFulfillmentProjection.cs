using AICOS.BuildingBlocks.Exceptions;
using AICOS.Contracts.Serving;
using AICOS.Ordering.Api.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AICOS.Ordering.Api.Features.Fulfillment;

public enum SnapshotOutcome { Applied, Duplicate, Superseded }

/// <summary>Read projection only: never calls Order.Deliver, payment handlers or CloseIfSettled.</summary>
public sealed class OrderFulfillmentProjection
{
    public Guid OrderId { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid BranchId { get; private set; }
    public Guid TicketId { get; private set; }
    public string ManifestHash { get; private set; } = "";
    public long SourceVersion { get; private set; }
    public string SnapshotHash { get; private set; } = "";
    public int Total { get; private set; }
    public int Served { get; private set; }
    public int Review { get; private set; }
    public string SnapshotJson { get; private set; } = "{}";
    public DateTime? FulfilledAtUtc { get; private set; }
    public DateTime LastVerifiedAtUtc { get; private set; }

    public SnapshotOutcome Apply(FulfillmentSnapshot snapshot, DateTime verifiedAt)
    {
        Validate(snapshot);
        if (verifiedAt.Kind != DateTimeKind.Utc) throw new ArgumentException("Verification time must be UTC.");
        if (SourceVersion > 0 && (TenantId != snapshot.TenantId || BranchId != snapshot.BranchId
            || OrderId != snapshot.OrderId || TicketId != snapshot.TicketId || ManifestHash != snapshot.ManifestHash
            || Total != snapshot.Total))
            throw new ConflictException("Fulfillment identity differs.", "fulfillment.identity_conflict");
        var hash = FulfillmentSnapshotHash.Compute(snapshot);
        if (snapshot.Version < SourceVersion) return SnapshotOutcome.Superseded;
        if (snapshot.Version == SourceVersion)
        {
            if (hash != SnapshotHash) throw new ConflictException("Same version has different content.", "fulfillment.version_conflict");
            LastVerifiedAtUtc = verifiedAt;
            return SnapshotOutcome.Duplicate;
        }
        // A final proof cannot be undone by a later snapshot. Changes require a new workflow.
        if (snapshot.Served < Served || (FulfilledAtUtc is not null
            && (snapshot.Served != snapshot.Total || snapshot.FulfilledAtUtc != FulfilledAtUtc)))
            throw new ConflictException("Fulfilled state cannot regress.", "fulfillment.terminal_conflict");
        OrderId = snapshot.OrderId; TenantId = snapshot.TenantId; BranchId = snapshot.BranchId;
        TicketId = snapshot.TicketId; ManifestHash = snapshot.ManifestHash; SourceVersion = snapshot.Version;
        SnapshotHash = hash; Total = snapshot.Total; Served = snapshot.Served; Review = snapshot.Review;
        FulfilledAtUtc = snapshot.FulfilledAtUtc; LastVerifiedAtUtc = verifiedAt;
        SnapshotJson = System.Text.Json.JsonSerializer.Serialize(snapshot);
        return SnapshotOutcome.Applied;
    }

    private static void Validate(FulfillmentSnapshot s)
    {
        var counts = new[] { s.Waiting, s.Available, s.Reserved, s.Carrying, s.Served, s.Review };
        if (new[] { s.EventId, s.TenantId, s.BranchId, s.OrderId, s.TicketId }.Contains(Guid.Empty)
            || s.Version < 1 || s.Total is < 1 or > 4950 || counts.Any(x => x < 0)
            || counts.Sum(x => (long)x) != s.Total || s.ManifestHash is null || s.ManifestHash.Length != 64
            || !s.ManifestHash.All(Uri.IsHexDigit)
            || (s.Served == s.Total) != (s.FulfilledAtUtc is not null)
            || (s.FulfilledAtUtc is { } at && at.Kind != DateTimeKind.Utc))
            throw new ValidationFailedException("snapshot", "Invalid fulfillment identity/counts/version/completion proof.");
    }
}

public sealed class FulfillmentReceipt
{
    public Guid EventId { get; init; }
    public Guid TenantId { get; init; }
    public Guid BranchId { get; init; }
    public Guid OrderId { get; init; }
    public long SourceVersion { get; init; }
    public string PayloadHash { get; init; } = "";
    public string Result { get; init; } = "";
    public DateTime AppliedAtUtc { get; init; }
}

internal sealed class OrderFulfillmentConfiguration : IEntityTypeConfiguration<OrderFulfillmentProjection>
{
    public void Configure(EntityTypeBuilder<OrderFulfillmentProjection> b)
    {
        b.ToTable("order_fulfillment_projection", t =>
        {
            t.HasCheckConstraint("ck_order_fulfillment_summary", "source_version > 0 AND total BETWEEN 1 AND 4950 AND served BETWEEN 0 AND total AND review BETWEEN 0 AND total - served");
            t.HasCheckConstraint("ck_order_fulfillment_completed", "(served = total) = (fulfilled_at_utc IS NOT NULL)");
            t.HasCheckConstraint("ck_order_fulfillment_hash", "length(manifest_hash) = 64 AND length(snapshot_hash) = 64 AND jsonb_typeof(snapshot_json) = 'object'");
        });
        b.HasKey(x => x.OrderId); b.Property(x => x.OrderId).ValueGeneratedNever();
        b.Property(x => x.ManifestHash).HasMaxLength(64); b.Property(x => x.SnapshotHash).HasMaxLength(64);
        b.Property(x => x.SourceVersion).IsConcurrencyToken(); b.Property(x => x.SnapshotJson).HasColumnType("jsonb");
        b.HasOne<Order>().WithOne().HasForeignKey<OrderFulfillmentProjection>(x => x.OrderId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.TenantId, x.BranchId, x.LastVerifiedAtUtc });
    }
}

internal sealed class FulfillmentReceiptConfiguration : IEntityTypeConfiguration<FulfillmentReceipt>
{
    public void Configure(EntityTypeBuilder<FulfillmentReceipt> b)
    {
        b.ToTable("fulfillment_receipts", t =>
        {
            t.HasCheckConstraint("ck_fulfillment_receipt_payload", "source_version > 0 AND length(payload_hash) = 64 AND result IN ('Applied','Duplicate','Superseded')");
        });
        b.HasKey(x => x.EventId); b.Property(x => x.EventId).ValueGeneratedNever();
        b.Property(x => x.PayloadHash).HasMaxLength(64); b.Property(x => x.Result).HasMaxLength(20);
        b.HasOne<Order>().WithMany().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.TenantId, x.OrderId, x.SourceVersion });
    }
}
