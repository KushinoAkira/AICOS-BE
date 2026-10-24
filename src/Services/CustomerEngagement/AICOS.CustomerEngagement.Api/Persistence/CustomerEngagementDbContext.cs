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

    public DbSet<LoyaltyRule> LoyaltyRules => Set<LoyaltyRule>();
}

internal sealed class CustomerConfiguration : IEntityTypeConfiguration<Customer>
{
    public void Configure(EntityTypeBuilder<Customer> builder)
    {
        builder.ToTable("customers", t =>
        {
            t.HasCheckConstraint("ck_customers_points_non_negative", "points_balance >= 0 AND lifetime_points >= 0");
            t.HasCheckConstraint("ck_customers_sugar_percent_range", "preferred_sugar_percent IS NULL OR preferred_sugar_percent BETWEEN 0 AND 100");
        });
        builder.Property(c => c.Id).ValueGeneratedNever();
        builder.Property(c => c.Version).IsConcurrencyToken();
        builder.Property(c => c.FullName).HasMaxLength(255).IsRequired();
        builder.Property(c => c.Email).HasMaxLength(255).IsRequired();
        builder.Property(c => c.Phone).HasMaxLength(20);
        builder.Property(c => c.PreferredIce).HasMaxLength(20);
        builder.Property(c => c.PreferenceNotes).HasMaxLength(500);
        builder.Property(c => c.Tier).HasConversion<string>().HasMaxLength(20);
        builder.HasIndex(c => c.UserId).IsUnique();
        builder.HasIndex(c => c.TenantId);
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
        builder.HasIndex(t => new { t.OrderId, t.Type }).IsUnique();   // one Earn and one Reverse per order
        builder.HasIndex(t => t.CustomerId);
    }
}

internal sealed class VoucherConfiguration : IEntityTypeConfiguration<Voucher>
{
    public void Configure(EntityTypeBuilder<Voucher> builder)
    {
        builder.ToTable("vouchers", t =>
        {
            t.HasCheckConstraint("ck_vouchers_value_positive", "value > 0");
            t.HasCheckConstraint("ck_vouchers_min_order_non_negative", "min_order_amount IS NULL OR min_order_amount >= 0");
        });
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
        builder.HasIndex(v => new { v.TenantId, v.Code }).IsUnique();
        builder.HasIndex(v => new { v.TenantId, v.BranchId, v.Status });
        builder.HasIndex(v => v.IssuedToCustomerId);
    }
}

internal sealed class LoyaltyRuleConfiguration : IEntityTypeConfiguration<LoyaltyRule>
{
    public void Configure(EntityTypeBuilder<LoyaltyRule> builder)
    {
        builder.ToTable("loyalty_rules", t =>
        {
            t.HasCheckConstraint("ck_loyalty_rules_rates_positive", "amount_per_point > 0 AND redeem_value_per_point > 0 AND min_points_to_redeem >= 1");
            t.HasCheckConstraint("ck_loyalty_rules_tier_order", "silver_threshold >= 1 AND gold_threshold > silver_threshold");
        });
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).ValueGeneratedNever();
        builder.Property(r => r.AmountPerPoint).HasPrecision(14, 2);
        builder.Property(r => r.RedeemValuePerPoint).HasPrecision(14, 2);
        builder.HasIndex(r => r.TenantId).IsUnique();
    }
}
