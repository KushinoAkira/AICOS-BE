using AICOS.InventoryPurchasing.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AICOS.InventoryPurchasing.Api.Persistence.Configurations;

internal sealed class SupplierConfiguration : IEntityTypeConfiguration<Supplier>
{
    public void Configure(EntityTypeBuilder<Supplier> builder)
    {
        builder.ToTable("suppliers");
        builder.Property(s => s.Id).ValueGeneratedNever();
        builder.Property(s => s.Name).HasMaxLength(Supplier.NameMaxLength).IsRequired();
        builder.Property(s => s.ContactName).HasMaxLength(255);
        builder.Property(s => s.Phone).HasMaxLength(20);
        builder.Property(s => s.Email).HasMaxLength(255);
        builder.Property(s => s.Address).HasMaxLength(500);
        builder.Property(s => s.Status).HasConversion<string>().HasMaxLength(20);
        builder.HasIndex(s => new { s.BranchId, s.Name }).IsUnique();
    }
}

internal sealed class IngredientConfiguration : IEntityTypeConfiguration<Ingredient>
{
    public void Configure(EntityTypeBuilder<Ingredient> builder)
    {
        builder.ToTable("ingredients");
        builder.Property(i => i.Id).ValueGeneratedNever();
        builder.Property(i => i.Name).HasMaxLength(Ingredient.NameMaxLength).IsRequired();
        builder.Property(i => i.Unit).HasMaxLength(20).IsRequired();
        builder.Property(i => i.ReorderLevel).HasPrecision(14, 3);
        builder.Property(i => i.QuantityOnHand).HasPrecision(14, 3);
        builder.Property(i => i.Version).IsConcurrencyToken();
        builder.Ignore(i => i.IsLowStock);
        builder.HasIndex(i => new { i.BranchId, i.Name }).IsUnique();
    }
}

internal sealed class StockTransactionConfiguration : IEntityTypeConfiguration<StockTransaction>
{
    public void Configure(EntityTypeBuilder<StockTransaction> builder)
    {
        builder.ToTable("stock_transactions");
        builder.Property(t => t.Id).ValueGeneratedNever();
        builder.Property(t => t.Type).HasConversion<string>().HasMaxLength(20);
        builder.Property(t => t.QuantityDelta).HasPrecision(14, 3);
        builder.Property(t => t.QuantityAfter).HasPrecision(14, 3);
        builder.Property(t => t.DocumentNo).HasMaxLength(50).IsRequired();
        builder.Property(t => t.Note).HasMaxLength(500);
        builder.HasOne<Ingredient>().WithMany().HasForeignKey(t => t.IngredientId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(t => new { t.BranchId, t.CreatedAtUtc });
        builder.HasIndex(t => t.IngredientId);
    }
}

internal sealed class RecipeItemConfiguration : IEntityTypeConfiguration<RecipeItem>
{
    public void Configure(EntityTypeBuilder<RecipeItem> builder)
    {
        builder.ToTable("recipes");
        builder.Property(r => r.Id).ValueGeneratedNever();
        builder.Property(r => r.Quantity).HasPrecision(14, 3);
        builder.HasOne<Ingredient>().WithMany().HasForeignKey(r => r.IngredientId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(r => new { r.ProductId, r.IngredientId }).IsUnique();
    }
}

internal sealed class PurchaseOrderConfiguration : IEntityTypeConfiguration<PurchaseOrder>
{
    public void Configure(EntityTypeBuilder<PurchaseOrder> builder)
    {
        builder.ToTable("purchase_orders");
        builder.Property(p => p.Id).ValueGeneratedNever();
        builder.Property(p => p.Code).HasMaxLength(30).IsRequired();
        builder.Property(p => p.Status).HasConversion<string>().HasMaxLength(20);
        builder.Property(p => p.Note).HasMaxLength(1000);
        builder.Property(p => p.RejectReason).HasMaxLength(500);
        builder.Property(p => p.TotalAmount).HasPrecision(14, 2);
        builder.HasOne<Supplier>().WithMany().HasForeignKey(p => p.SupplierId).OnDelete(DeleteBehavior.Restrict);
        builder.HasMany(p => p.Items).WithOne().HasForeignKey(i => i.PurchaseOrderId).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(p => p.Items).HasField("_items");
        builder.HasIndex(p => p.Code).IsUnique();
        builder.HasIndex(p => new { p.BranchId, p.Status });
    }
}

internal sealed class PurchaseOrderItemConfiguration : IEntityTypeConfiguration<PurchaseOrderItem>
{
    public void Configure(EntityTypeBuilder<PurchaseOrderItem> builder)
    {
        builder.ToTable("purchase_order_items");
        builder.Property(i => i.Id).ValueGeneratedNever();
        builder.Property(i => i.Quantity).HasPrecision(14, 3);
        builder.Property(i => i.UnitPrice).HasPrecision(14, 2);
        builder.HasOne<Ingredient>().WithMany().HasForeignKey(i => i.IngredientId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class StockAdjustmentConfiguration : IEntityTypeConfiguration<StockAdjustment>
{
    public void Configure(EntityTypeBuilder<StockAdjustment> builder)
    {
        builder.ToTable("stock_adjustments");
        builder.Property(a => a.Id).ValueGeneratedNever();
        builder.Property(a => a.Code).HasMaxLength(30).IsRequired();
        builder.Property(a => a.Note).HasMaxLength(1000);
        builder.Property(a => a.RejectReason).HasMaxLength(500);
        builder.Property(a => a.Status).HasConversion<string>().HasMaxLength(20);
        builder.HasMany(a => a.Lines).WithOne().HasForeignKey(l => l.StockAdjustmentId).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(a => a.Lines).HasField("_lines");
        builder.HasIndex(a => a.Code).IsUnique();
    }
}

internal sealed class StockAdjustmentLineConfiguration : IEntityTypeConfiguration<StockAdjustmentLine>
{
    public void Configure(EntityTypeBuilder<StockAdjustmentLine> builder)
    {
        builder.ToTable("stock_adjustment_lines");
        builder.Property(l => l.Id).ValueGeneratedNever();
        builder.Property(l => l.SystemQuantity).HasPrecision(14, 3);
        builder.Property(l => l.CountedQuantity).HasPrecision(14, 3);
        builder.Ignore(l => l.Difference);
        builder.HasOne<Ingredient>().WithMany().HasForeignKey(l => l.IngredientId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class WasteReportConfiguration : IEntityTypeConfiguration<WasteReport>
{
    public void Configure(EntityTypeBuilder<WasteReport> builder)
    {
        builder.ToTable("waste_reports");
        builder.Property(w => w.Id).ValueGeneratedNever();
        builder.Property(w => w.Code).HasMaxLength(30).IsRequired();
        builder.Property(w => w.Quantity).HasPrecision(14, 3);
        builder.Property(w => w.Reason).HasMaxLength(500).IsRequired();
        builder.Property(w => w.EvidenceImageUrl).HasMaxLength(1000);
        builder.HasOne<Ingredient>().WithMany().HasForeignKey(w => w.IngredientId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(w => w.Code).IsUnique();
    }
}
