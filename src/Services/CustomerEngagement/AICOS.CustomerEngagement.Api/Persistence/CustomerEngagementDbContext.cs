using AICOS.BuildingBlocks.Persistence;
using AICOS.CustomerEngagement.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AICOS.CustomerEngagement.Api.Persistence;

public sealed class CustomerEngagementDbContext(DbContextOptions<CustomerEngagementDbContext> options) : AicosDbContext(options)
{
    protected override string Schema => CustomerEngagementService.Info.Schema;

    public DbSet<Customer> Customers => Set<Customer>();

    public DbSet<LoyaltyTransaction> LoyaltyTransactions => Set<LoyaltyTransaction>();

    public DbSet<Voucher> Vouchers => Set<Voucher>();
}

internal sealed class CustomerConfiguration : IEntityTypeConfiguration<Customer>
{
    public void Configure(EntityTypeBuilder<Customer> builder)
    {
        builder.ToTable("customers");
        builder.Property(c => c.Id).ValueGeneratedNever();
        builder.Property(c => c.FullName).HasMaxLength(255).IsRequired();
        builder.Property(c => c.Email).HasMaxLength(255).IsRequired();
        builder.Property(c => c.Phone).HasMaxLength(20);
        builder.Property(c => c.PreferredIce).HasMaxLength(20);
        builder.Property(c => c.PreferenceNotes).HasMaxLength(500);
        builder.Property(c => c.Tier).HasConversion<string>().HasMaxLength(20);
        builder.HasIndex(c => c.UserId).IsUnique();
        builder.HasIndex(c => c.Phone);
    }
}

internal sealed class LoyaltyTransactionConfiguration : IEntityTypeConfiguration<LoyaltyTransaction>
{
    public void Configure(EntityTypeBuilder<LoyaltyTransaction> builder)
    {
        builder.ToTable("loyalty_transactions");
        builder.Property(t => t.Id).ValueGeneratedNever();
        builder.Property(t => t.Type).HasConversion<string>().HasMaxLength(20);
        builder.Property(t => t.Note).HasMaxLength(500);
        builder.HasOne<Customer>().WithMany().HasForeignKey(t => t.CustomerId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(t => t.OrderId).IsUnique();
        builder.HasIndex(t => t.CustomerId);
    }
}

internal sealed class VoucherConfiguration : IEntityTypeConfiguration<Voucher>
{
    public void Configure(EntityTypeBuilder<Voucher> builder)
    {
        builder.ToTable("vouchers");
        builder.Property(v => v.Id).ValueGeneratedNever();
        builder.Property(v => v.Code).HasMaxLength(30).IsRequired();
        builder.Property(v => v.Type).HasConversion<string>().HasMaxLength(20);
        builder.Property(v => v.DiscountType).HasConversion<string>().HasMaxLength(20);
        builder.Property(v => v.Status).HasConversion<string>().HasMaxLength(20);
        builder.Property(v => v.Value).HasPrecision(14, 2);
        builder.Property(v => v.MinOrderAmount).HasPrecision(14, 2);
        builder.Property(v => v.RedeemedDiscount).HasPrecision(14, 2);
        builder.Property(v => v.IssuedToName).HasMaxLength(255);
        builder.Property(v => v.RedeemedOrderReference).HasMaxLength(50);
        builder.Property(v => v.RevokeReason).HasMaxLength(500);
        builder.Property(v => v.Version).IsConcurrencyToken();
        builder.HasIndex(v => v.Code).IsUnique();
        builder.HasIndex(v => new { v.BranchId, v.Status });
    }
}
