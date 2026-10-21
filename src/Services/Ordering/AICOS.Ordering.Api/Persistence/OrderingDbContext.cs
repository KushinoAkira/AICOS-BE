using AICOS.BuildingBlocks.Persistence;
using AICOS.Ordering.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AICOS.Ordering.Api.Persistence;

public sealed class OrderingDbContext(DbContextOptions<OrderingDbContext> options) : AicosDbContext(options)
{
    protected override string Schema => OrderingService.Info.Schema;

    public DbSet<PickupSlotSetting> PickupSlotSettings => Set<PickupSlotSetting>();
}

internal sealed class PickupSlotSettingConfiguration : IEntityTypeConfiguration<PickupSlotSetting>
{
    public void Configure(EntityTypeBuilder<PickupSlotSetting> builder)
    {
        builder.ToTable("pickup_slot_settings", t =>
        {
            t.HasCheckConstraint("ck_pickup_slot_settings_ranges", "slot_minutes BETWEEN 5 AND 120 AND capacity_per_slot BETWEEN 1 AND 500 AND cutoff_minutes BETWEEN 0 AND 240 AND advance_days BETWEEN 0 AND 14");
        });
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).ValueGeneratedNever();
        builder.HasIndex(s => new { s.TenantId, s.BranchId }).IsUnique();
    }
}
