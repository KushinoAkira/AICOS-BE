using AICOS.BuildingBlocks.Persistence;
using AICOS.TableReservation.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AICOS.TableReservation.Api.Persistence;

public sealed class TableReservationDbContext(DbContextOptions<TableReservationDbContext> options) : AicosDbContext(options)
{
    protected override string Schema => TableReservationService.Info.Schema;

    public DbSet<TableArea> TableAreas => Set<TableArea>();

    public DbSet<DiningTable> DiningTables => Set<DiningTable>();
}

internal sealed class TableAreaConfiguration : IEntityTypeConfiguration<TableArea>
{
    public void Configure(EntityTypeBuilder<TableArea> builder)
    {
        builder.ToTable("table_areas");
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).ValueGeneratedNever();
        builder.Property(a => a.Name).HasMaxLength(TableArea.NameMaxLength).IsRequired();
        builder.HasIndex(a => new { a.BranchId, a.Name }).IsUnique();
        builder.HasIndex(a => a.TenantId);
    }
}

internal sealed class DiningTableConfiguration : IEntityTypeConfiguration<DiningTable>
{
    public void Configure(EntityTypeBuilder<DiningTable> builder)
    {
        builder.ToTable("dining_tables");
        builder.Property(t => t.Id).ValueGeneratedNever();
        builder.Property(t => t.TableCode).HasMaxLength(DiningTable.CodeMaxLength).IsRequired();
        builder.Property(t => t.Status).HasConversion<string>().HasMaxLength(20);
        builder.Property(t => t.QrToken).HasMaxLength(64).IsRequired();

        builder.HasOne<TableArea>().WithMany().HasForeignKey(t => t.AreaId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(t => new { t.BranchId, t.TableCode }).IsUnique();
        builder.HasIndex(t => t.QrToken).IsUnique();
        builder.HasIndex(t => t.TenantId);
        builder.HasIndex(t => t.AreaId);
    }
}
