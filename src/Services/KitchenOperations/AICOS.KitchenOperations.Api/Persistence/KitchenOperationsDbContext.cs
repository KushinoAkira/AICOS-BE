using AICOS.BuildingBlocks.Persistence;
using AICOS.KitchenOperations.Api.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AICOS.KitchenOperations.Api.Persistence;

public sealed class KitchenOperationsDbContext(DbContextOptions<KitchenOperationsDbContext> options) : AicosDbContext(options)
{
    protected override string Schema => KitchenOperationsService.Info.Schema;

    public DbSet<KitchenTicket> Tickets => Set<KitchenTicket>();

    public DbSet<KitchenTicketItem> TicketItems => Set<KitchenTicketItem>();

    public DbSet<CupSticker> CupStickers => Set<CupSticker>();
}

internal sealed class KitchenTicketConfiguration : IEntityTypeConfiguration<KitchenTicket>
{
    public void Configure(EntityTypeBuilder<KitchenTicket> builder)
    {
        builder.ToTable("kitchen_tickets");
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).ValueGeneratedNever();
        builder.Property(t => t.OrderCode).HasMaxLength(40).IsRequired();
        builder.Property(t => t.TableNumber).HasMaxLength(20);
        builder.Property(t => t.OrderType).HasMaxLength(20).IsRequired();
        builder.Property(t => t.LastRemakeReason).HasMaxLength(200);
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
    }
}
