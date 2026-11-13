using AICOS.Ordering.Api.Features.SessionBilling;
using AICOS.BuildingBlocks.Security;
using AICOS.BuildingBlocks.Persistence;
using AICOS.Ordering.Api.Domain;
using AICOS.Ordering.Api.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using AICOS.Ordering.Api.Features.KitchenAdmission;
using AICOS.Ordering.Api.Features.Fulfillment;

namespace AICOS.Ordering.Api.Persistence;

public sealed class OrderingDbContext(DbContextOptions<OrderingDbContext> options, IConfiguration? configuration = null, TimeProvider? clock = null) : AicosDbContext(options)
{
    internal bool BillEnabled => configuration?.GetValue<bool>("SessionBilling:Enabled") == true;
    public DbSet<SessionCheckout> SessionCheckouts => Set<SessionCheckout>();
    public DbSet<AdmissionOutbox> AdmissionOutbox => Set<AdmissionOutbox>();
    public DbSet<AdmissionReconcileState> AdmissionReconcile => Set<AdmissionReconcileState>();
    public DbSet<OrderFulfillmentProjection> FulfillmentProjection => Set<OrderFulfillmentProjection>();
    public DbSet<FulfillmentReceipt> FulfillmentReceipts => Set<FulfillmentReceipt>();

    private void CaptureAdmissions()
    {
        foreach (var entry in ChangeTracker.Entries<Order>().Where(x => x.State == EntityState.Added).ToArray())
        {
            var cup = entry.Entity.FulfillmentMode == AICOS.Contracts.Serving.FulfillmentModes.CupV1;
            if (configuration?.GetValue<bool>(cup ? "CupServing:CaptureEnabled" : "KitchenAdmission:CaptureEnabled") != true) continue;
            if (AdmissionOutbox.Local.Any(x => x.OrderId == entry.Entity.Id && x.TenantId == entry.Entity.TenantId)) continue;
            AdmissionOutbox.Add(AdmissionProjection.Capture(entry.Entity, (clock ?? TimeProvider.System).GetUtcNow().UtcDateTime));
        }
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess) => SaveChangesAsync(acceptAllChangesOnSuccess).GetAwaiter().GetResult();
    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken ct = default)
    {
        async Task<int> Save()
        {
            if (configuration is not null && BillingMachineAuth.Enabled(configuration)) await SessionBillingWriteGuard.CheckAsync(this, ct);
            CaptureAdmissions();
            return await base.SaveChangesAsync(acceptAllChangesOnSuccess, ct);
        }
        if (configuration is null || !BillingMachineAuth.Enabled(configuration) || !Database.IsRelational() || Database.CurrentTransaction is not null)
            return await Save();
        return await Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var transaction = await Database.BeginTransactionAsync(ct);
            var result = await Save();
            await transaction.CommitAsync(ct);
            return result;
        });
    }

    protected override string Schema => OrderingService.Info.Schema;

    public DbSet<PickupSlotSetting> PickupSlotSettings => Set<PickupSlotSetting>();

    public DbSet<Order> Orders => Set<Order>();

    public DbSet<OrderItem> OrderItems => Set<OrderItem>();

    public DbSet<OrderFeedback> OrderFeedbacks => Set<OrderFeedback>();

    public DbSet<TableSession> TableSessions => Set<TableSession>();
}

internal sealed class PickupSlotSettingConfiguration : IEntityTypeConfiguration<PickupSlotSetting>
{
    public void Configure(EntityTypeBuilder<PickupSlotSetting> builder)
    {
        builder.ToTable("pickup_slot_settings", t =>
        {
            t.HasCheckConstraint("ck_pickup_slot_settings_ranges", "slot_minutes BETWEEN 5 AND 120 AND capacity_per_slot BETWEEN 1 AND 500 AND cutoff_minutes BETWEEN 0 AND 240 AND advance_days BETWEEN 0 AND 14");
        });
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).ValueGeneratedNever();
        builder.HasIndex(s => new { s.TenantId, s.BranchId }).IsUnique();
    }
}

internal sealed class OrderConfiguration : IEntityTypeConfiguration<Order>
{
    public void Configure(EntityTypeBuilder<Order> builder)
    {
        builder.ToTable("orders", t =>
        {
            t.HasCheckConstraint("ck_orders_amounts", "discount_amount >= 0 AND refunded_amount >= 0");
            t.HasCheckConstraint("ck_order_fulfillment_mode", "fulfillment_mode IN ('WholeOrder','CupV1')");
        });
        builder.HasKey(o => o.Id);
        builder.Property(o => o.Id).ValueGeneratedNever();
        builder.Property(o => o.OrderCode).HasMaxLength(40).IsRequired();
        builder.Property(o => o.FulfillmentMode).HasMaxLength(20).HasDefaultValue(AICOS.Contracts.Serving.FulfillmentModes.WholeOrder);
        builder.Property(o => o.TableNumber).HasMaxLength(20);
        builder.Property(o => o.DeliveryAddress).HasMaxLength(300);
        builder.Property(o => o.Type).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(o => o.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(o => o.PaymentStatus).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(o => o.PaymentMethod).HasConversion<string>().HasMaxLength(20);
        builder.Property(o => o.PaymentProvider).HasMaxLength(20);
        builder.Property(o => o.RefundStatus).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(o => o.CancelledReason).HasMaxLength(300);
        builder.Property(o => o.Note).HasMaxLength(Order.NoteMaxLength);
        builder.Property(o => o.PromotionCode).HasMaxLength(40);
        builder.Property(o => o.DiscountAmount).HasPrecision(18, 0);
        builder.Property(o => o.RefundedAmount).HasPrecision(18, 0);
        builder.Ignore(o => o.OriginalAmount);
        builder.Ignore(o => o.FinalAmount);
        builder.Ignore(o => o.TotalAmount);
        builder.Ignore(o => o.IsPaid);
        builder.Ignore(o => o.IsVoided);
        builder.Ignore(o => o.EffectiveRefundStatus);

        builder.HasMany(o => o.Items).WithOne().HasForeignKey(i => i.OrderId).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(o => o.Items).UsePropertyAccessMode(PropertyAccessMode.Field).HasField("_items");

        builder.HasIndex(o => new { o.TenantId, o.OrderCode }).IsUnique();
        builder.HasIndex(o => new { o.TenantId, o.BranchId, o.CreatedAtUtc });
        builder.HasIndex(o => new { o.TenantId, o.BranchId, o.Status });
        builder.HasIndex(o => o.PaymentId);
        builder.HasIndex(o => o.CustomerId);
        builder.HasIndex(o => o.TableSessionId).HasFilter("table_session_id IS NOT NULL");
        builder.Property(o => o.IdempotencyKey).HasMaxLength(80);
        builder.HasIndex(o => new { o.TenantId, o.CustomerId, o.IdempotencyKey }).IsUnique().HasFilter("idempotency_key IS NOT NULL");
        builder.HasIndex(o => new { o.TenantId, o.BranchId, o.PickupSlotStartUtc });
        builder.HasIndex(o => o.PaymentDueAtUtc).HasFilter("payment_due_at_utc IS NOT NULL");
    }
}

internal sealed class OrderItemConfiguration : IEntityTypeConfiguration<OrderItem>
{
    public void Configure(EntityTypeBuilder<OrderItem> builder)
    {
        builder.ToTable("order_items", t =>
        {
            t.HasCheckConstraint("ck_order_items_quantity_price", "quantity > 0 AND unit_price >= 0");
        });
        builder.HasKey(i => i.Id);
        builder.Property(i => i.Id).ValueGeneratedNever();
        builder.Property(i => i.ProductName).HasMaxLength(200).IsRequired();
        builder.Property(i => i.UnitPrice).HasPrecision(18, 0);
        builder.Property(i => i.Note).HasMaxLength(300);
        builder.Ignore(i => i.TotalPrice);
        builder.Ignore(i => i.OptionsText);
        builder.HasMany(i => i.Options).WithOne().HasForeignKey(o => o.OrderItemId).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(i => i.Options).UsePropertyAccessMode(PropertyAccessMode.Field).HasField("_options");
        builder.HasIndex(i => i.OrderId);
    }
}

internal sealed class OrderFeedbackConfiguration : IEntityTypeConfiguration<OrderFeedback>
{
    public void Configure(EntityTypeBuilder<OrderFeedback> builder)
    {
        builder.ToTable("order_feedbacks", t =>
        {
            t.HasCheckConstraint("ck_order_feedbacks_rating", "rating BETWEEN 1 AND 5");
        });
        builder.HasKey(f => f.Id);
        builder.Property(f => f.Id).ValueGeneratedNever();
        builder.Property(f => f.OrderCode).HasMaxLength(40).IsRequired();
        builder.Property(f => f.Comment).HasMaxLength(OrderFeedback.CommentMaxLength);
        builder.HasIndex(f => f.OrderId).IsUnique();
        builder.HasIndex(f => new { f.TenantId, f.BranchId, f.CreatedAtUtc });
        builder.HasIndex(f => f.CustomerId);
    }
}

internal sealed class OrderLineOptionConfiguration : IEntityTypeConfiguration<OrderLineOption>
{
    public void Configure(EntityTypeBuilder<OrderLineOption> builder)
    {
        builder.ToTable("order_item_options", t =>
        {
            t.HasCheckConstraint("ck_order_item_options_price", "price_delta >= 0");
        });
        builder.HasKey(o => o.Id);
        builder.Property(o => o.Id).ValueGeneratedNever();
        builder.Property(o => o.GroupName).HasMaxLength(100).IsRequired();
        builder.Property(o => o.Name).HasMaxLength(100).IsRequired();
        builder.Property(o => o.PriceDelta).HasPrecision(18, 0);
        builder.HasIndex(o => o.OrderItemId);
    }
}

internal sealed class TableSessionConfiguration : IEntityTypeConfiguration<TableSession>
{
    public void Configure(EntityTypeBuilder<TableSession> builder)
    {
        builder.Property(s => s.BillRequestedMethod).HasMaxLength(20);
        builder.ToTable("table_sessions");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).ValueGeneratedNever();
        builder.Property(s => s.TableCode).HasMaxLength(40).IsRequired();
        builder.Property(s => s.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(s => s.CloseReason).HasConversion<string>().HasMaxLength(20);
        builder.HasIndex(s => new { s.TenantId, s.TableId }).IsUnique().HasFilter("status = 'Open'");   // one open session per table
        builder.HasIndex(s => new { s.TenantId, s.BranchId, s.Status });
        builder.HasIndex(s => s.LastActivityAtUtc).HasFilter("status = 'Open'");
    }
}
