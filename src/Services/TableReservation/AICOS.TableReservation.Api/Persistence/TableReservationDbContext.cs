using AICOS.BuildingBlocks.Persistence;
using AICOS.TableReservation.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AICOS.TableReservation.Api.Persistence;

public sealed class TableReservationDbContext(DbContextOptions<TableReservationDbContext> options) : AicosDbContext(options)
{
    protected override string Schema => TableReservationService.Info.Schema;

    public DbSet<DiningTable> DiningTables => Set<DiningTable>();

    public DbSet<Reservation> Reservations => Set<Reservation>();
}

internal sealed class DiningTableConfiguration : IEntityTypeConfiguration<DiningTable>
{
    public void Configure(EntityTypeBuilder<DiningTable> builder)
    {
        builder.ToTable("dining_tables");
        builder.Property(t => t.Id).ValueGeneratedNever();
        builder.Property(t => t.TableCode).HasMaxLength(DiningTable.CodeMaxLength).IsRequired();
        builder.Property(t => t.Area).HasMaxLength(DiningTable.AreaMaxLength);
        builder.Property(t => t.Status).HasConversion<string>().HasMaxLength(20);
        builder.Property(t => t.QrToken).HasMaxLength(64).IsRequired();
        builder.HasIndex(t => new { t.BranchId, t.TableCode }).IsUnique();
        builder.HasIndex(t => t.QrToken).IsUnique();
    }
}

internal sealed class ReservationConfiguration : IEntityTypeConfiguration<Reservation>
{
    public void Configure(EntityTypeBuilder<Reservation> builder)
    {
        builder.ToTable("reservations");
        builder.Property(r => r.Id).ValueGeneratedNever();
        builder.Property(r => r.CustomerName).HasMaxLength(100).IsRequired();
        builder.Property(r => r.CustomerPhone).HasMaxLength(20).IsRequired();
        builder.Property(r => r.Status).HasConversion<string>().HasMaxLength(20);
        builder.Property(r => r.Note).HasMaxLength(500);
        builder.Property(r => r.CancelReason).HasMaxLength(500);
        builder.HasOne<DiningTable>().WithMany().HasForeignKey(r => r.TableId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(r => new { r.TableId, r.ReservedAtUtc });
        builder.HasIndex(r => new { r.BranchId, r.ReservedAtUtc });
        builder.HasIndex(r => r.CustomerUserId);
    }
}
