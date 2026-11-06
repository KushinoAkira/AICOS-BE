using AICOS.KitchenOperations.Api.Domain.Entities;
using AICOS.KitchenOperations.Api.Features.CupServing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AICOS.KitchenOperations.Api.Persistence.CupServing;

internal sealed class CupTaskConfiguration : IEntityTypeConfiguration<CupTask>
{
    public void Configure(EntityTypeBuilder<CupTask> b)
    {
        b.ToTable("cup_tasks", t =>
        {
            t.HasCheckConstraint("ck_cup_task_numbers", "unit_index > 0 AND order_cup_index > 0 AND preparation_cycle > 0 AND version >= 0 AND fence >= 0");
            t.HasCheckConstraint("ck_cup_task_states", "preparation IN ('Pending','Brewing','Ready') AND service IN ('Waiting','Available','Reserved','Carrying','Served','Review')");
            t.HasCheckConstraint("ck_cup_task_served", "(service = 'Served') = (served_at_utc IS NOT NULL AND served_by IS NOT NULL)");
            t.HasCheckConstraint("ck_cup_task_ready", "service NOT IN ('Available','Reserved','Carrying','Served') OR (preparation = 'Ready' AND ready_at_utc IS NOT NULL)");
            t.HasCheckConstraint("ck_cup_task_review", "(service = 'Review') = (review_reason IS NOT NULL)");
        });
        b.HasKey(x => x.Id); b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Preparation).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.Service).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.ReviewReason).HasMaxLength(200);
        b.Property(x => x.Version).IsConcurrencyToken();
        b.HasIndex(x => new { x.TenantId, x.OrderId, x.OrderItemId, x.UnitIndex }).IsUnique();
        b.HasIndex(x => new { x.TicketId, x.OrderCupIndex }).IsUnique();
        b.HasIndex(x => new { x.TenantId, x.BranchId, x.Service, x.CreatedAtUtc });
        b.HasOne<KitchenTicket>().WithMany().HasForeignKey(x => x.TicketId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<KitchenTicketItem>().WithMany().HasForeignKey(x => x.TicketItemId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class CupClaimConfiguration : IEntityTypeConfiguration<CupClaim>
{
    public void Configure(EntityTypeBuilder<CupClaim> b)
    {
        b.ToTable("cup_claims", t =>
        {
            t.HasCheckConstraint("ck_cup_claim_lease", "lease_expires_at_utc > created_at_utc AND preparation_cycle > 0 AND fence > 0 AND version >= 0");
            t.HasCheckConstraint("ck_cup_claim_state", "state IN ('Reserved','Carrying','Overdue','Served','Released','Overridden')");
            t.HasCheckConstraint("ck_cup_claim_terminal", "(state IN ('Served','Released','Overridden')) = (ended_at_utc IS NOT NULL)");
        });
        b.HasKey(x => x.Id); b.Property(x => x.Id).ValueGeneratedNever();
        b.Ignore(x => x.IsLive); b.Property(x => x.Version).IsConcurrencyToken();
        b.Property(x => x.State).HasConversion<string>().HasMaxLength(20);
        b.HasOne<CupTask>().WithMany().HasForeignKey(x => x.CupId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.CupId).IsUnique().HasFilter("state IN ('Reserved','Carrying','Overdue')")
            .HasDatabaseName("uq_cup_claim_live");
        b.HasIndex(x => new { x.TenantId, x.BranchId, x.OwnerUserId, x.State });
        b.HasIndex(x => new { x.State, x.LeaseExpiresAtUtc });
    }
}

internal sealed class CupOperationConfiguration : IEntityTypeConfiguration<CupOperation>
{
    public void Configure(EntityTypeBuilder<CupOperation> b)
    {
        b.ToTable("cup_operations", t =>
        {
            t.HasCheckConstraint("ck_cup_operation_audit", "version_after > version_before AND version_before >= 0 AND length(operation_key) > 0 AND length(request_hash) = 64");
            t.HasCheckConstraint("ck_cup_operation_json", "jsonb_typeof(before_json) = 'object' AND jsonb_typeof(after_json) = 'object' AND jsonb_typeof(result_json) = 'object'");
        });
        b.HasKey(x => x.Id); b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.OperationKey).HasMaxLength(80); b.Property(x => x.Command).HasMaxLength(40);
        b.Property(x => x.RequestHash).HasMaxLength(64); b.Property(x => x.Reason).HasMaxLength(500);
        b.Property(x => x.BeforeJson).HasColumnType("jsonb"); b.Property(x => x.AfterJson).HasColumnType("jsonb");
        b.Property(x => x.ResultJson).HasColumnType("jsonb");
        b.HasIndex(x => new { x.TenantId, x.ActorId, x.OperationKey }).IsUnique();
        b.HasIndex(x => new { x.CupId, x.CommittedAtUtc });
        b.HasOne<CupTask>().WithMany().HasForeignKey(x => x.CupId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class FulfillmentOutboxConfiguration : IEntityTypeConfiguration<FulfillmentOutbox>
{
    public void Configure(EntityTypeBuilder<FulfillmentOutbox> b)
    {
        b.ToTable("fulfillment_outbox", t =>
        {
            t.HasCheckConstraint("ck_fulfillment_outbox_state", "state IN ('PENDING','IN_FLIGHT','ACKED','NEEDS_REVIEW') AND source_version > 0 AND attempt_count >= 0");
            t.HasCheckConstraint("ck_fulfillment_outbox_lease", "(state = 'IN_FLIGHT' AND lease_token IS NOT NULL AND lease_until_utc IS NOT NULL) OR (state <> 'IN_FLIGHT' AND lease_token IS NULL AND lease_until_utc IS NULL)");
            t.HasCheckConstraint("ck_fulfillment_outbox_ack", "(state = 'ACKED') = (acknowledged_at_utc IS NOT NULL)");
            t.HasCheckConstraint("ck_fulfillment_outbox_payload", "jsonb_typeof(payload) = 'object' AND length(payload_hash) = 64");
        });
        b.HasKey(x => x.EventId); b.Property(x => x.EventId).ValueGeneratedNever();
        b.Property(x => x.Payload).HasColumnType("jsonb"); b.Property(x => x.PayloadHash).HasMaxLength(64);
        b.Property(x => x.State).HasMaxLength(20); b.Property(x => x.LastErrorCode).HasMaxLength(100);
        b.HasIndex(x => new { x.TenantId, x.OrderId, x.SourceVersion }).IsUnique();
        b.HasIndex(x => new { x.NextAttemptAtUtc, x.LeaseUntilUtc }).HasFilter("state IN ('PENDING','IN_FLIGHT')");
    }
}
