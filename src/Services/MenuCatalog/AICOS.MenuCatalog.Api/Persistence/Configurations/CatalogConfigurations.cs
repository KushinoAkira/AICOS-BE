using AICOS.MenuCatalog.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AICOS.MenuCatalog.Api.Persistence.Configurations;

internal sealed class CategoryConfiguration : IEntityTypeConfiguration<Category>
{
    public void Configure(EntityTypeBuilder<Category> builder)
    {
        builder.ToTable("categories");
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).ValueGeneratedNever();
        builder.Property(c => c.Name).HasMaxLength(Category.NameMaxLength).IsRequired();
        builder.Property(c => c.Status).HasConversion<string>().HasMaxLength(20);
        builder.HasIndex(c => new { c.BranchId, c.Name }).IsUnique();
    }
}

internal sealed class ProductConfiguration : IEntityTypeConfiguration<Product>
{
    public void Configure(EntityTypeBuilder<Product> builder)
    {
        builder.ToTable("products");
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedNever();
        builder.Property(p => p.Name).HasMaxLength(Product.NameMaxLength).IsRequired();
        builder.Property(p => p.Description).HasMaxLength(Product.DescriptionMaxLength);
        builder.Property(p => p.ImageUrl).HasMaxLength(1000);
        builder.Property(p => p.BasePrice).HasPrecision(12, 2);

        // Stored as one comma-separated text column; portable across PostgreSQL and the in-memory test provider.
        builder.Property(p => p.Tags)
            .HasConversion(
                tags => string.Join(',', tags),
                text => text.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList(),
                new ValueComparer<List<string>>((a, b) => a!.SequenceEqual(b!), t => t.Aggregate(0, (h, s) => HashCode.Combine(h, s.GetHashCode())), t => t.ToList()))
            .HasMaxLength(500);

        builder.HasOne<Category>().WithMany().HasForeignKey(p => p.CategoryId).OnDelete(DeleteBehavior.Restrict);
        builder.HasMany(p => p.OptionGroups).WithOne().HasForeignKey(g => g.ProductId).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(p => p.OptionGroups).HasField("_optionGroups");

        builder.HasIndex(p => new { p.BranchId, p.Name });
        builder.HasQueryFilter(p => p.DeletedAtUtc == null);
    }
}

internal sealed class OptionGroupConfiguration : IEntityTypeConfiguration<OptionGroup>
{
    public void Configure(EntityTypeBuilder<OptionGroup> builder)
    {
        builder.ToTable("option_groups");
        builder.HasKey(g => g.Id);
        builder.Property(g => g.Id).ValueGeneratedNever();
        builder.Property(g => g.Name).HasMaxLength(OptionGroup.NameMaxLength).IsRequired();
        builder.Property(g => g.SelectionType).HasConversion<string>().HasMaxLength(10);
        builder.HasMany(g => g.Options).WithOne().HasForeignKey(o => o.OptionGroupId).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(g => g.Options).HasField("_options");
    }
}

internal sealed class ProductOptionConfiguration : IEntityTypeConfiguration<ProductOption>
{
    public void Configure(EntityTypeBuilder<ProductOption> builder)
    {
        builder.ToTable("product_options");
        builder.HasKey(o => o.Id);
        builder.Property(o => o.Id).ValueGeneratedNever();
        builder.Property(o => o.Name).HasMaxLength(ProductOption.NameMaxLength).IsRequired();
        builder.Property(o => o.PriceDelta).HasPrecision(12, 2);
    }
}

internal sealed class PromotionConfiguration : IEntityTypeConfiguration<Promotion>
{
    public void Configure(EntityTypeBuilder<Promotion> builder)
    {
        builder.ToTable("promotions");
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedNever();
        builder.Property(p => p.Name).HasMaxLength(Promotion.NameMaxLength).IsRequired();
        builder.Property(p => p.Description).HasMaxLength(2000);
        builder.Property(p => p.DiscountType).HasConversion<string>().HasMaxLength(20);
        builder.Property(p => p.DiscountValue).HasPrecision(12, 2);
        builder.Property(p => p.Status).HasConversion<string>().HasMaxLength(20);
        builder.HasMany(p => p.Products).WithOne().HasForeignKey(pp => pp.PromotionId).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(p => p.Products).HasField("_products");
        builder.HasIndex(p => new { p.BranchId, p.Status });
    }
}

internal sealed class PromotionProductConfiguration : IEntityTypeConfiguration<PromotionProduct>
{
    public void Configure(EntityTypeBuilder<PromotionProduct> builder)
    {
        builder.ToTable("promotion_products");
        builder.HasKey(pp => new { pp.PromotionId, pp.ProductId });
        builder.HasOne<Product>().WithMany().HasForeignKey(pp => pp.ProductId).OnDelete(DeleteBehavior.Cascade);
    }
}
