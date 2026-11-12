using AICOS.BuildingBlocks.Persistence;
using AICOS.KitchenOperations.Api.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using AICOS.KitchenOperations.Api.Features.KitchenAdmission;
using AICOS.KitchenOperations.Api.Features.CupServing;

namespace AICOS.KitchenOperations.Api.Persistence;

public sealed class KitchenOperationsDbContext(DbContextOptions<KitchenOperationsDbContext> options) : AicosDbContext(options)
{
    protected override string Schema => KitchenOperationsService.Info.Schema;

    public DbSet<KitchenTicket> Tickets => Set<KitchenTicket>();

    public DbSet<KitchenTicketItem> TicketItems => Set<KitchenTicketItem>();

    public DbSet<CupSticker> CupStickers => Set<CupSticker>();
    public DbSet<AdmissionReceipt> AdmissionReceipts => Set<AdmissionReceipt>();
    public DbSet<AICOS.KitchenOperations.Api.Features.EarlyPayment> EarlyPayments => Set<AICOS.KitchenOperations.Api.Features.EarlyPayment>();
    public DbSet<CupTask> CupTasks => Set<CupTask>();
    public DbSet<CupClaim> CupClaims => Set<CupClaim>();
    public DbSet<CupOperation> CupOperations => Set<CupOperation>();
    public DbSet<FulfillmentOutbox> FulfillmentOutbox => Set<FulfillmentOutbox>();

    private void RequireImmutableOperations()
    {
        if (ChangeTracker.Entries<CupOperation>().Any(e => e.State is EntityState.Modified or EntityState.Deleted))
            throw new InvalidOperationException("Cup operation audit records are append-only.");
    }
    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    { RequireImmutableOperations(); return base.SaveChanges(acceptAllChangesOnSuccess); }
    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken ct = default)
    { RequireImmutableOperations(); return base.SaveChangesAsync(acceptAllChangesOnSuccess, ct); }
}

internal sealed class KitchenTicketConfiguration : IEntityTypeConfiguration<KitchenTicket>
{
    public void Configure(EntityTypeBuilder<KitchenTicket> builder)
    {
        builder.ToTable("kitchen_tickets", t => t.HasCheckConstraint("ck_ticket_fulfillment_mode",
            "fulfillment_mode IN ('WholeOrder','CupV1') AND fulfillment_version >= 0"));
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).ValueGeneratedNever();
        builder.Property(t => t.OrderCode).HasMaxLength(40).IsRequired();
        builder.Property(t => t.FulfillmentMode).HasMaxLength(20).HasDefaultValue(AICOS.Contracts.Serving.FulfillmentModes.WholeOrder);
        builder.Property(t => t.TableNumber).HasMaxLength(20);
        builder.Property(t => t.OrderType).HasMaxLength(20).IsRequired();
        builder.Property(t => t.LastRemakeReason).HasMaxLength(200);
        builder.Property(t => t.RecoveryHoldReason).HasMaxLength(200);
        builder.Property(t => t.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Ignore(t => t.IsPriority);
        builder.Ignore(t => t.TotalCups);
        builder.Ignore(t => t.IsEligibleForBar);

        builder.HasMany(t => t.Items).WithOne().HasForeignKey(i => i.TicketId).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(t => t.Items).UsePropertyAccessMode(PropertyAccessMode.Field).HasField("_items");
        builder.HasMany(t => t.Stickers).WithOne().HasForeignKey(s => s.TicketId).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(t => t.Stickers).UsePropertyAccessMode(PropertyAccessMode.Field).HasField("_stickers");

        builder.HasIndex(t => new { t.TenantId, t.OrderId }).IsUnique();
        builder.HasIndex(t => new { t.TenantId, t.BranchId, t.Status, t.CreatedAtUtc });
    }
}

internal sealed class KitchenTicketItemConfiguration : IEntityTypeConfiguration<KitchenTicketItem>
{
    public void Configure(EntityTypeBuilder<KitchenTicketItem> builder)
    {
        builder.ToTable("kitchen_ticket_items", t =>
        {
            t.HasCheckConstraint("ck_kitchen_ticket_items_quantity", "quantity > 0");
        });
        builder.HasKey(i => i.Id);
        builder.Property(i => i.Id).ValueGeneratedNever();
        builder.Property(i => i.ProductName).HasMaxLength(200).IsRequired();
        builder.Property(i => i.Note).HasMaxLength(300);
        builder.HasIndex(i => i.TicketId);
        builder.HasIndex(i => new { i.TicketId, i.OrderItemId }).IsUnique().HasFilter("order_item_id IS NOT NULL");
    }
}

internal sealed class CupStickerConfiguration : IEntityTypeConfiguration<CupSticker>
{
    public void Configure(EntityTypeBuilder<CupSticker> builder)
    {
        builder.ToTable("cup_stickers");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).ValueGeneratedNever();
        builder.Property(s => s.OrderCode).HasMaxLength(40).IsRequired();
        builder.Property(s => s.TableNumber).HasMaxLength(20);
        builder.Property(s => s.ProductName).HasMaxLength(200).IsRequired();
        builder.Property(s => s.Customizations).HasMaxLength(300);
        builder.HasIndex(s => s.TicketId);
        builder.HasIndex(s => s.CupTaskId).IsUnique().HasFilter("cup_task_id IS NOT NULL");
        builder.HasOne<CupTask>().WithMany().HasForeignKey(s => s.CupTaskId).OnDelete(DeleteBehavior.Restrict);
    }
}
