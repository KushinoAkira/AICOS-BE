using AICOS.KitchenOperations.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AICOS.KitchenOperations.Api.Persistence;

internal sealed class PreparationOrderConfiguration : IEntityTypeConfiguration<PreparationOrder>
{
    public void Configure(EntityTypeBuilder<PreparationOrder> builder)
    {
        builder.ToTable("orders");
        builder.HasKey(order => order.Id);
        builder.Property(order => order.OrderCode).HasMaxLength(50).IsRequired();
        builder.Property(order => order.OrderType).HasMaxLength(30).IsRequired();
        builder.Property(order => order.Status).HasMaxLength(30);
        builder.Property(order => order.CreatedAt).HasColumnType("timestamp without time zone");
        builder
            .HasMany(order => order.Items)
            .WithOne(item => item.Order)
            .HasForeignKey(item => item.OrderId)
            .OnDelete(DeleteBehavior.NoAction);
    }
}

internal sealed class PreparationItemConfiguration : IEntityTypeConfiguration<PreparationItem>
{
    public void Configure(EntityTypeBuilder<PreparationItem> builder)
    {
        builder.ToTable("order_items");
        builder.HasKey(item => item.Id);
        builder.Property(item => item.ProductName).HasMaxLength(200).IsRequired();
        builder.Property(item => item.VariantName).HasMaxLength(50);
        builder.Property(item => item.Status).HasMaxLength(30);
        builder
            .HasMany(item => item.Events)
            .WithOne(preparationEvent => preparationEvent.Item)
            .HasForeignKey(preparationEvent => preparationEvent.OrderItemId)
            .OnDelete(DeleteBehavior.NoAction);
    }
}

internal sealed class PreparationEventConfiguration : IEntityTypeConfiguration<PreparationEvent>
{
    public void Configure(EntityTypeBuilder<PreparationEvent> builder)
    {
        builder.ToTable("order_item_events");
        builder.HasKey(preparationEvent => preparationEvent.Id);
        builder.Property(preparationEvent => preparationEvent.EventType).HasMaxLength(50).IsRequired();
        builder.Property(preparationEvent => preparationEvent.OldStatus).HasMaxLength(30);
        builder.Property(preparationEvent => preparationEvent.NewStatus).HasMaxLength(30);
        builder.Property(preparationEvent => preparationEvent.CreatedAt)
            .HasColumnType("timestamp without time zone");
    }
}
