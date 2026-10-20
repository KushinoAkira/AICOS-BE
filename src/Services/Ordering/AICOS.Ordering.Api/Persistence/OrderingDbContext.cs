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
        builder.ToTable("pickup_slot_settings");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).ValueGeneratedNever();
        builder.HasIndex(s => new { s.TenantId, s.BranchId }).IsUnique();
    }
}
