using AICOS.IdentityOrganization.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AICOS.IdentityOrganization.Api.Persistence.Configurations;

internal sealed class SubscriptionPlanConfiguration : IEntityTypeConfiguration<SubscriptionPlan>
{
    public void Configure(EntityTypeBuilder<SubscriptionPlan> builder)
    {
        builder.ToTable("subscription_plans", t =>
        {
            t.HasCheckConstraint("ck_subscription_plans_fee_non_negative", "fee >= 0");
            t.HasCheckConstraint("ck_subscription_plans_limits_positive", "(max_branches IS NULL OR max_branches > 0) AND (max_staff_accounts IS NULL OR max_staff_accounts > 0)");
        });
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedNever();

        builder.Property(p => p.Name).HasMaxLength(SubscriptionPlan.NameMaxLength).IsRequired();
        builder.Property(p => p.Tier).HasMaxLength(SubscriptionPlan.TierMaxLength).IsRequired();
        builder.Property(p => p.Fee).HasPrecision(14, 2);
        builder.Property(p => p.BillingPeriod).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(p => p.Description).HasMaxLength(SubscriptionPlan.DescriptionMaxLength);

        builder.HasIndex(p => p.Name).IsUnique();
        builder.HasIndex(p => p.IsActive);
    }
}

internal sealed class TenantSubscriptionConfiguration : IEntityTypeConfiguration<TenantSubscription>
{
    public void Configure(EntityTypeBuilder<TenantSubscription> builder)
    {
        builder.ToTable("tenant_subscriptions");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).ValueGeneratedNever();

        builder.Property(s => s.PlanName).HasMaxLength(SubscriptionPlan.NameMaxLength).IsRequired();
        builder.Property(s => s.Fee).HasPrecision(14, 2);
        builder.Property(s => s.PaidAmount).HasPrecision(14, 2);
        builder.Property(s => s.BillingPeriod).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(s => s.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(s => s.PaymentStatus).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Ignore(s => s.Outstanding);

        builder.HasIndex(s => s.TenantId);
        builder.HasIndex(s => s.PeriodEndUtc);

        builder.HasOne<Tenant>().WithMany().HasForeignKey(s => s.TenantId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<SubscriptionPlan>().WithMany().HasForeignKey(s => s.PlanId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class SubscriptionPaymentConfiguration : IEntityTypeConfiguration<SubscriptionPayment>
{
    public void Configure(EntityTypeBuilder<SubscriptionPayment> builder)
    {
        builder.ToTable("subscription_payments", t =>
        {
            t.HasCheckConstraint("ck_subscription_payments_amount_positive", "amount > 0");
        });
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedNever();

        builder.Property(p => p.Amount).HasPrecision(14, 2);
        builder.Property(p => p.Method).HasMaxLength(SubscriptionPayment.MethodMaxLength).IsRequired();
        builder.Property(p => p.Reference).HasMaxLength(SubscriptionPayment.ReferenceMaxLength);

        builder.HasIndex(p => p.SubscriptionId);
        builder.HasIndex(p => p.TenantId);
        builder.HasIndex(p => p.PaidAtUtc);

        builder.HasOne<TenantSubscription>().WithMany().HasForeignKey(p => p.SubscriptionId).OnDelete(DeleteBehavior.Restrict);
    }
}
