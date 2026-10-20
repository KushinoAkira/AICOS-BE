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
