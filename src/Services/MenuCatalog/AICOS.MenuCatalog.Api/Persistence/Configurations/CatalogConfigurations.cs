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
        builder.HasIndex(c => new { c.TenantId, c.Name }).IsUnique();
    }
}

internal sealed class ProductConfiguration : IEntityTypeConfiguration<Product>
{
    public void Configure(EntityTypeBuilder<Product> builder)
    {
        builder.ToTable("products", t =>
        {
            t.HasCheckConstraint("ck_products_base_price_positive", "base_price > 0");
        });
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedNever();
        builder.Property(p => p.Name).HasMaxLength(Product.NameMaxLength).IsRequired();
        builder.Property(p => p.Description).HasMaxLength(Product.DescriptionMaxLength);
        builder.Property(p => p.ImageUrl).HasMaxLength(400_000);   // a link or an uploaded picture (data URL)
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

        builder.HasIndex(p => new { p.TenantId, p.Name });
        builder.HasIndex(p => p.CategoryId);
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
        builder.Property(g => g.Kind).HasConversion<string>().HasMaxLength(10).IsRequired();
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
        // No foreign key on RequiresOptionId: an edit rebuilds all option groups of a product together.
    }
}

internal sealed class BranchProductConfiguration : IEntityTypeConfiguration<BranchProduct>
{
    public void Configure(EntityTypeBuilder<BranchProduct> builder)
    {
        builder.ToTable("branch_products", t =>
        {
            t.HasCheckConstraint("ck_branch_products_price_override_positive", "price_override IS NULL OR price_override > 0");
        });
        builder.HasKey(b => b.Id);
        builder.Property(b => b.Id).ValueGeneratedNever();
        builder.Property(b => b.PriceOverride).HasPrecision(12, 2);

        builder.HasOne<Product>().WithMany().HasForeignKey(b => b.ProductId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(b => new { b.BranchId, b.ProductId }).IsUnique();
        builder.HasIndex(b => new { b.TenantId, b.BranchId });
    }
}
