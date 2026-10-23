using AICOS.BuildingBlocks.Persistence;
using AICOS.ReportingAnalytics.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AICOS.ReportingAnalytics.Api.Persistence;

public sealed class ReportingAnalyticsDbContext(DbContextOptions<ReportingAnalyticsDbContext> options) : AicosDbContext(options)
{
    protected override string Schema => ReportingAnalyticsService.Info.Schema;

    public DbSet<OrderFact> OrderFacts => Set<OrderFact>();
}

internal sealed class OrderFactConfiguration : IEntityTypeConfiguration<OrderFact>
{
    public void Configure(EntityTypeBuilder<OrderFact> builder)
    {
        builder.ToTable("order_facts", t =>
        {
            t.HasCheckConstraint("ck_order_facts_amounts", "total_amount >= 0 AND paid_amount >= 0 AND refunded_amount >= 0");
        });
        builder.HasKey(f => f.Id);
        builder.Property(f => f.Id).ValueGeneratedNever();
        builder.Property(f => f.OrderType).HasMaxLength(20).IsRequired();
        builder.Property(f => f.PaymentMethod).HasMaxLength(20);
        builder.Property(f => f.TotalAmount).HasPrecision(18, 0);
        builder.Property(f => f.PaidAmount).HasPrecision(18, 0);
        builder.Property(f => f.RefundedAmount).HasPrecision(18, 0);
        builder.Ignore(f => f.IsPaid);
        builder.Ignore(f => f.Counts);

        builder.HasIndex(f => new { f.TenantId, f.BranchId, f.PlacedAtUtc });
        builder.HasIndex(f => new { f.TenantId, f.BranchId, f.PaidAtUtc });
    }
}
