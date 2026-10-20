using AICOS.BuildingBlocks.Persistence;
using AICOS.Payments.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AICOS.Payments.Api.Persistence;

public sealed class PaymentsDbContext(DbContextOptions<PaymentsDbContext> options) : AicosDbContext(options)
{
    protected override string Schema => PaymentsService.Info.Schema;

    public DbSet<PaymentProvider> PaymentProviders => Set<PaymentProvider>();
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
