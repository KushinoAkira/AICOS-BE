using System.Runtime.CompilerServices;
using System.Text.Json;
using AICOS.Contracts.KitchenAdmission;
using AICOS.Contracts.Serving;
using AICOS.Ordering.Api.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AICOS.Ordering.Api.Features.KitchenAdmission;

public sealed class AdmissionOutbox
{
    public Guid EventId { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid BranchId { get; set; }
    public Guid OrderId { get; set; }
    public string ConsumerKey { get; set; } = AdmissionProtocol.Consumer;
    public int SchemaVersion { get; set; } = 1;
    public string CreationPayload { get; set; } = "";
    public byte[] CreationSha256 { get; set; } = [];
    public string Origin { get; set; } = "CAPTURE";
    public string State { get; set; } = "PENDING";
    public DateTime CreatedAtUtc { get; set; }
    public DateTime NextAttemptAtUtc { get; set; }
    public int AttemptCount { get; set; }
    public Guid? LeaseToken { get; set; }
    public DateTime? LeaseUntilUtc { get; set; }
    public DateTime? AcknowledgedAtUtc { get; set; }
    public Guid? AcknowledgedTicketId { get; set; }
    public string? LastErrorCode { get; set; }
}

public sealed class AdmissionReconcileState
{
    public string ConsumerKey { get; set; } = AdmissionProtocol.Consumer;
    public Guid CycleId { get; set; } = Guid.NewGuid();
    public Guid? UpperOrderId { get; set; }
    public Guid? LastOrderId { get; set; }
    public DateTime CycleStartedAtUtc { get; set; }
    public DateTime? LastCompletedAtUtc { get; set; }
    public Guid? LeaseToken { get; set; }
    public DateTime? LeaseUntilUtc { get; set; }
}

public static class AdmissionProjection
{
    private static readonly ConditionalWeakTable<Order, AdmissionOutbox> Captured = new();
    public static AdmissionCreation Creation(Order order) => new(order.OrderCode, order.Items.Select(i =>
    {
        var note = string.Join(" | ", new[] { i.OptionsText, i.Note }.Where(s => !string.IsNullOrWhiteSpace(s)));
        return new AdmissionItem(i.ProductId, i.ProductName, i.Quantity, note.Length == 0 ? null : note[..Math.Min(note.Length, 300)]);
    }).ToArray());

    public static AdmissionOutbox Capture(Order order, DateTime now) => Captured.GetValue(order, o => New(o, now));

    public static AdmissionOutbox New(Order order, DateTime now, string origin = "CAPTURE")
    {
        if (order.FulfillmentMode == FulfillmentModes.CupV1)
        {
            var notes = Creation(order).Items.ToArray();
            var manifest = new CupManifest(order.OrderCode, order.FulfillmentMode, order.TableId, order.TableSessionId,
                order.Items.Select((i, index) => new CupAdmissionItem(i.Id, i.ProductId, i.ProductName, i.Quantity, notes[index].Note)).ToArray());
            return new AdmissionOutbox
            {
                TenantId = order.TenantId,
                BranchId = order.BranchId,
                OrderId = order.Id,
                ConsumerKey = CupAdmissionProtocol.Consumer,
                SchemaVersion = 2,
                CreationPayload = JsonSerializer.Serialize(manifest, AdmissionProtocol.Json),
                CreationSha256 = Convert.FromHexString(CupAdmissionProtocol.Hash(order.TenantId, order.BranchId, order.Id, manifest)),
                CreatedAtUtc = now,
                NextAttemptAtUtc = now,
                Origin = origin
            };
        }
        var creation = Creation(order);
        return new AdmissionOutbox
        {
            TenantId = order.TenantId,
            BranchId = order.BranchId,
            OrderId = order.Id,
            CreationPayload = JsonSerializer.Serialize(creation, AdmissionProtocol.Json),
            CreationSha256 = AdmissionProtocol.Hash(order.TenantId, order.BranchId, order.Id, creation),
            CreatedAtUtc = now,
            NextAttemptAtUtc = now,
            Origin = origin,
        };
    }

    public static AdmissionRequest Request(AdmissionOutbox row, Order order) => new(row.EventId, row.SchemaVersion,
        row.ConsumerKey, row.TenantId, row.BranchId, row.OrderId,
        JsonSerializer.Deserialize<AdmissionCreation>(row.CreationPayload, AdmissionProtocol.Json)!, Convert.ToHexString(row.CreationSha256),
        new AdmissionFacts(order.Type.ToString(), order.Status.ToString(), order.IsPaid, order.TableNumber,
            order.CreatedAtUtc, order.PickupSlotStartUtc, order.DeliveredAtUtc, order.CancelledAtUtc, order.PayFirst));

    public static CupAdmissionRequest CupRequest(AdmissionOutbox row, Order order) => new(row.EventId,
        row.TenantId, row.BranchId, row.OrderId, JsonSerializer.Deserialize<CupManifest>(row.CreationPayload, AdmissionProtocol.Json)!,
        Convert.ToHexString(row.CreationSha256), new AdmissionFacts(order.Type.ToString(), order.Status.ToString(), order.IsPaid,
            order.TableNumber, order.CreatedAtUtc, order.PickupSlotStartUtc, order.DeliveredAtUtc, order.CancelledAtUtc, order.PayFirst));
}

internal sealed class AdmissionOutboxConfiguration : IEntityTypeConfiguration<AdmissionOutbox>
{
    public void Configure(EntityTypeBuilder<AdmissionOutbox> b)
    {
        b.ToTable("kitchen_admission_outbox", t =>
        {
            t.HasCheckConstraint("ck_kitchen_admission_state", "state IN ('PENDING','IN_FLIGHT','ACKED','NEEDS_REVIEW')");
            t.HasCheckConstraint("ck_kitchen_admission_lease", "(state = 'IN_FLIGHT' AND lease_token IS NOT NULL AND lease_until_utc IS NOT NULL) OR (state <> 'IN_FLIGHT' AND lease_token IS NULL AND lease_until_utc IS NULL)");
            t.HasCheckConstraint("ck_kitchen_admission_ack", "(state = 'ACKED' AND acknowledged_at_utc IS NOT NULL AND acknowledged_ticket_id IS NOT NULL) OR (state <> 'ACKED' AND acknowledged_at_utc IS NULL AND acknowledged_ticket_id IS NULL)");
            t.HasCheckConstraint("ck_kitchen_admission_payload", "jsonb_typeof(creation_payload) = 'object' AND octet_length(creation_sha256) = 32 AND schema_version > 0 AND attempt_count >= 0 AND origin IN ('CAPTURE','RECONCILE')");
        });
        b.HasKey(x => x.EventId); b.Property(x => x.EventId).ValueGeneratedNever();
        b.Property(x => x.ConsumerKey).HasMaxLength(80);
        b.Property(x => x.CreationPayload).HasColumnType("jsonb");
        b.Property(x => x.State).HasMaxLength(20); b.Property(x => x.Origin).HasMaxLength(20);
        b.Property(x => x.LastErrorCode).HasMaxLength(100);
        b.HasIndex(x => new { x.TenantId, x.OrderId, x.ConsumerKey }).IsUnique().HasDatabaseName("uq_kitchen_admission_order");
        b.HasIndex(x => new { x.NextAttemptAtUtc, x.LeaseUntilUtc, x.EventId }).HasFilter("state IN ('PENDING','IN_FLIGHT')").HasDatabaseName("ix_kitchen_admission_due");
        b.HasIndex(x => new { x.TenantId, x.BranchId, x.State, x.CreatedAtUtc }).HasDatabaseName("ix_kitchen_admission_scope_status");
    }
}

internal sealed class AdmissionReconcileConfiguration : IEntityTypeConfiguration<AdmissionReconcileState>
{
    public void Configure(EntityTypeBuilder<AdmissionReconcileState> b)
    {
        b.ToTable("kitchen_admission_reconcile_state", t =>
        {
            t.HasCheckConstraint("ck_kitchen_reconcile_lease", "(lease_token IS NULL AND lease_until_utc IS NULL) OR (lease_token IS NOT NULL AND lease_until_utc IS NOT NULL)");
            t.HasCheckConstraint("ck_kitchen_reconcile_cursor", "last_order_id IS NULL OR (upper_order_id IS NOT NULL AND last_order_id <= upper_order_id)");
        });
        b.HasKey(x => x.ConsumerKey); b.Property(x => x.ConsumerKey).HasMaxLength(80).ValueGeneratedNever();
    }
}
