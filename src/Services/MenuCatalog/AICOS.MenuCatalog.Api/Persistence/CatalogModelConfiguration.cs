using AICOS.MenuCatalog.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AICOS.MenuCatalog.Api.Persistence;

internal sealed class CategoryConfiguration : IEntityTypeConfiguration<Category>
{
    public void Configure(EntityTypeBuilder<Category> builder)
    {
        builder.ToTable("categories");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Name).HasMaxLength(100).IsRequired();
        builder.Property(x => x.SortOrder).HasDefaultValue(0);
        builder.Property(x => x.IsActive).HasDefaultValue(true);
        builder.HasMany(x => x.Products)
            .WithOne(x => x.Category)
            .HasForeignKey(x => x.CategoryId)
            .OnDelete(DeleteBehavior.NoAction);
    }
}

internal sealed class ProductConfiguration : IEntityTypeConfiguration<Product>
{
    public void Configure(EntityTypeBuilder<Product> builder)
    {
        builder.ToTable("products");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Name).HasMaxLength(200).IsRequired();
        builder.Property(x => x.IsActive).HasDefaultValue(true);
        builder.Property(x => x.CreatedAt)
            .HasColumnType("timestamp without time zone")
            .HasDefaultValueSql("now()");
        ConfigureProductRelationship(builder.HasMany(x => x.Variants).WithOne(x => x.Product));
        ConfigureProductRelationship(builder.HasMany(x => x.OptionGroups).WithOne(x => x.Product));
        ConfigureProductRelationship(builder.HasMany(x => x.BranchProducts).WithOne(x => x.Product));
    }

    private static void ConfigureProductRelationship<T>(
        Microsoft.EntityFrameworkCore.Metadata.Builders.ReferenceCollectionBuilder<Product, T> relationship)
        where T : class =>
        relationship.HasForeignKey("ProductId").OnDelete(DeleteBehavior.NoAction);
}

internal sealed class ProductVariantConfiguration : IEntityTypeConfiguration<ProductVariant>
{
    public void Configure(EntityTypeBuilder<ProductVariant> builder)
    {
        builder.ToTable("product_variants");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.SizeName).HasMaxLength(50).IsRequired();
        builder.Property(x => x.Price).HasPrecision(18, 2).IsRequired();
        builder.Property(x => x.IsActive).HasDefaultValue(true);
        builder.HasIndex(x => new { x.ProductId, x.SizeName }).IsUnique();
    }
}

internal sealed class ProductOptionGroupConfiguration : IEntityTypeConfiguration<ProductOptionGroup>
{
    public void Configure(EntityTypeBuilder<ProductOptionGroup> builder)
    {
        builder.ToTable("product_option_groups");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Name).HasMaxLength(100).IsRequired();
        builder.Property(x => x.MinSelect).HasDefaultValue(0);
        builder.Property(x => x.MaxSelect).HasDefaultValue(1);
        builder.HasMany(x => x.Options)
            .WithOne(x => x.Group)
            .HasForeignKey(x => x.GroupId)
            .OnDelete(DeleteBehavior.NoAction);
    }
}

internal sealed class ProductOptionConfiguration : IEntityTypeConfiguration<ProductOption>
{
    public void Configure(EntityTypeBuilder<ProductOption> builder)
    {
        builder.ToTable("product_options");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Name).HasMaxLength(100).IsRequired();
        builder.Property(x => x.ExtraPrice).HasPrecision(18, 2).HasDefaultValue(0m);
        builder.Property(x => x.IsActive).HasDefaultValue(true);
    }
}

internal sealed class BranchProductConfiguration : IEntityTypeConfiguration<BranchProduct>
{
    public void Configure(EntityTypeBuilder<BranchProduct> builder)
    {
        builder.ToTable("branch_products");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.IsAvailable).HasDefaultValue(true);
        builder.HasIndex(x => new { x.BranchId, x.ProductId }).IsUnique();
    }
}
