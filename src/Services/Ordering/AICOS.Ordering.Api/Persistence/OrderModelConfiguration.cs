using AICOS.Ordering.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AICOS.Ordering.Api.Persistence;

internal sealed class OrderConfiguration : IEntityTypeConfiguration<Order>
{
    public void Configure(EntityTypeBuilder<Order> b)
    {
        b.ToTable("orders"); b.HasKey(x => x.Id);
        b.Property(x => x.OrderCode).HasMaxLength(50).IsRequired(); b.HasIndex(x => x.OrderCode).IsUnique();
        b.Property(x => x.OrderType).HasMaxLength(30).IsRequired(); b.Property(x => x.Status).HasMaxLength(30).HasDefaultValue(OrderStatuses.Pending);
        b.Property(x => x.PickupTime).HasColumnType("timestamp without time zone");
        b.Property(x => x.Subtotal).HasPrecision(18, 2).HasDefaultValue(0m); b.Property(x => x.TotalAmount).HasPrecision(18, 2).HasDefaultValue(0m);
        b.Property(x => x.CreatedAt).HasColumnType("timestamp without time zone").HasDefaultValueSql("now()");
        b.HasMany(x => x.Items).WithOne(x => x.Order).HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.NoAction);
    }
}

internal sealed class OrderItemConfiguration : IEntityTypeConfiguration<OrderItem>
{
    public void Configure(EntityTypeBuilder<OrderItem> b)
    {
        b.ToTable("order_items"); b.HasKey(x => x.Id);
        b.Property(x => x.ProductName).HasMaxLength(200).IsRequired(); b.Property(x => x.VariantName).HasMaxLength(50);
        b.Property(x => x.UnitPrice).HasPrecision(18, 2).IsRequired(); b.Property(x => x.Status).HasMaxLength(30).HasDefaultValue(OrderStatuses.Pending);
        b.HasMany(x => x.Options).WithOne(x => x.OrderItem).HasForeignKey(x => x.OrderItemId).OnDelete(DeleteBehavior.NoAction);
        b.HasMany(x => x.Events).WithOne(x => x.OrderItem).HasForeignKey(x => x.OrderItemId).OnDelete(DeleteBehavior.NoAction);
    }
}

internal sealed class OrderItemOptionConfiguration : IEntityTypeConfiguration<OrderItemOption>
{
    public void Configure(EntityTypeBuilder<OrderItemOption> b)
    {
        b.ToTable("order_item_options"); b.HasKey(x => x.Id);
        b.Property(x => x.OptionName).HasMaxLength(100).IsRequired(); b.Property(x => x.ExtraPrice).HasPrecision(18, 2).IsRequired().HasDefaultValue(0m);
    }
}

internal sealed class OrderItemEventConfiguration : IEntityTypeConfiguration<OrderItemEvent>
{
    public void Configure(EntityTypeBuilder<OrderItemEvent> b)
    {
        b.ToTable("order_item_events"); b.HasKey(x => x.Id);
        b.Property(x => x.EventType).HasMaxLength(50).IsRequired(); b.Property(x => x.OldStatus).HasMaxLength(30); b.Property(x => x.NewStatus).HasMaxLength(30);
        b.Property(x => x.CreatedAt).HasColumnType("timestamp without time zone").HasDefaultValueSql("now()");
    }
}
