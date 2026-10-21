using AICOS.BuildingBlocks.Persistence;
using AICOS.Payments.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AICOS.Payments.Api.Persistence;

public sealed class PaymentsDbContext(DbContextOptions<PaymentsDbContext> options) : AicosDbContext(options)
{
    protected override string Schema => PaymentsService.Info.Schema;

    public DbSet<PaymentProvider> PaymentProviders => Set<PaymentProvider>();

    public DbSet<CancellationPolicy> CancellationPolicies => Set<CancellationPolicy>();

    public DbSet<MerchantSetting> MerchantSettings => Set<MerchantSetting>();

    public DbSet<Payment> Payments => Set<Payment>();

    public DbSet<PaymentRefund> PaymentRefunds => Set<PaymentRefund>();

    public DbSet<PaymentWebhookEvent> WebhookEvents => Set<PaymentWebhookEvent>();
}

internal sealed class PaymentConfiguration : IEntityTypeConfiguration<Payment>
{
    public void Configure(EntityTypeBuilder<Payment> builder)
    {
        builder.ToTable("payments");
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedNever();
        builder.Property(p => p.Amount).HasPrecision(18, 0);
        builder.Property(p => p.RefundedAmount).HasPrecision(18, 0);
        builder.Property(p => p.Method).HasMaxLength(20).IsRequired();
        builder.Property(p => p.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(p => p.ProviderState).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(p => p.Description).HasMaxLength(200).IsRequired();
        builder.Property(p => p.TransactionRef).HasMaxLength(30).IsRequired();
        builder.Property(p => p.ProviderReference).HasMaxLength(100);
        builder.Property(p => p.CheckoutUrl).HasMaxLength(1000);
        builder.Property(p => p.FailureReason).HasMaxLength(300);
        builder.Ignore(p => p.IsCash);
        builder.Ignore(p => p.IsFinal);
        builder.Ignore(p => p.IsPaid);
        builder.Ignore(p => p.Refundable);

        builder.HasIndex(p => p.TransactionRef).IsUnique();
        builder.HasIndex(p => new { p.TenantId, p.OrderId });
        builder.HasIndex(p => new { p.TenantId, p.BranchId, p.CreatedAtUtc });
        builder.HasIndex(p => new { p.Status, p.ExpiresAtUtc });
    }
}

internal sealed class PaymentRefundConfiguration : IEntityTypeConfiguration<PaymentRefund>
{
    public void Configure(EntityTypeBuilder<PaymentRefund> builder)
    {
        builder.ToTable("payment_refunds");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).ValueGeneratedNever();
        builder.Property(r => r.Amount).HasPrecision(18, 0);
        builder.Property(r => r.Kind).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(r => r.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(r => r.Reason).HasMaxLength(300).IsRequired();
        builder.Property(r => r.DecisionNote).HasMaxLength(300);
        builder.Property(r => r.ProviderReference).HasMaxLength(100);

        builder.HasIndex(r => r.PaymentId);
        builder.HasIndex(r => new { r.TenantId, r.BranchId, r.Status });
    }
}

internal sealed class PaymentWebhookEventConfiguration : IEntityTypeConfiguration<PaymentWebhookEvent>
{
    public void Configure(EntityTypeBuilder<PaymentWebhookEvent> builder)
    {
        builder.ToTable("payment_webhook_events");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedNever();
        builder.Property(e => e.ProviderCode).HasMaxLength(20).IsRequired();
        builder.Property(e => e.EventId).HasMaxLength(100).IsRequired();
        builder.Property(e => e.TransactionRef).HasMaxLength(30).IsRequired();
        builder.Property(e => e.ReportedStatus).HasMaxLength(20).IsRequired();
        builder.Property(e => e.ReportedAmount).HasPrecision(18, 0);
        builder.Property(e => e.Outcome).HasMaxLength(30).IsRequired();

        builder.HasIndex(e => new { e.ProviderCode, e.EventId }).IsUnique();
        builder.HasIndex(e => new { e.TenantId, e.ReceivedAtUtc });
    }
}

internal sealed class PaymentProviderConfiguration : IEntityTypeConfiguration<PaymentProvider>
{
    public void Configure(EntityTypeBuilder<PaymentProvider> builder)
    {
        builder.ToTable("payment_providers");
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedNever();

        builder.Property(p => p.Code).HasMaxLength(20).IsRequired();
        builder.Property(p => p.DisplayName).HasMaxLength(PaymentProvider.DisplayNameMaxLength).IsRequired();
        builder.Property(p => p.Mode).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(p => p.MerchantId).HasMaxLength(PaymentProvider.MerchantIdMaxLength);
        builder.Property(p => p.ApiKeyEncrypted).HasMaxLength(1000);
        builder.Property(p => p.SecretKeyEncrypted).HasMaxLength(1000);
        builder.Property(p => p.CallbackUrl).HasMaxLength(PaymentProvider.CallbackUrlMaxLength);
        builder.Ignore(p => p.IsMock);
        builder.Ignore(p => p.HasCredentials);

        builder.HasIndex(p => p.Code).IsUnique();
    }
}

internal sealed class CancellationPolicyConfiguration : IEntityTypeConfiguration<CancellationPolicy>
{
    public void Configure(EntityTypeBuilder<CancellationPolicy> builder)
    {
        builder.ToTable("cancellation_policies");
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedNever();
        builder.Property(p => p.ExceptionApprover).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.HasIndex(p => p.TenantId).IsUnique();
    }
}

internal sealed class MerchantSettingConfiguration : IEntityTypeConfiguration<MerchantSetting>
{
    public void Configure(EntityTypeBuilder<MerchantSetting> builder)
    {
        builder.ToTable("merchant_settings");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).ValueGeneratedNever();
        builder.Property(s => s.ProviderCode).HasMaxLength(20).IsRequired();
        builder.Property(s => s.AccountName).HasMaxLength(MerchantSetting.AccountNameMaxLength);
        builder.Property(s => s.BankCode).HasMaxLength(MerchantSetting.BankCodeMaxLength);
        builder.Property(s => s.AccountNumberEncrypted).HasMaxLength(1000);
        builder.Property(s => s.AccountNumberHint).HasMaxLength(10);
        builder.Property(s => s.MerchantCode).HasMaxLength(MerchantSetting.MerchantCodeMaxLength);
        builder.Ignore(s => s.HasPaymentDetails);
        builder.HasIndex(s => new { s.TenantId, s.BranchId, s.ProviderCode }).IsUnique();
    }
}
