using AICOS.TableReservation.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AICOS.TableReservation.Api.Persistence;

internal sealed class TableAreaConfiguration : IEntityTypeConfiguration<TableArea>
{
    public void Configure(EntityTypeBuilder<TableArea> builder)
    {
        builder.ToTable("table_areas");
        builder.HasKey(area => area.Id);
        builder.Property(area => area.Name).HasMaxLength(100).IsRequired();
        builder.Property(area => area.SortOrder).HasDefaultValue(0);
        builder.HasMany(area => area.Tables)
            .WithOne(table => table.Area)
            .HasForeignKey(table => table.AreaId)
            .OnDelete(DeleteBehavior.NoAction);
    }
}

internal sealed class CafeTableConfiguration : IEntityTypeConfiguration<CafeTable>
{
    public void Configure(EntityTypeBuilder<CafeTable> builder)
    {
        builder.ToTable("cafe_tables");
        builder.HasKey(table => table.Id);
        builder.Property(table => table.TableNumber).HasMaxLength(30).IsRequired();
        builder.Property(table => table.Capacity).HasDefaultValue(2);
        builder.Property(table => table.Status)
            .HasMaxLength(30)
            .HasDefaultValue(CafeTableStatuses.Available);
        builder.Property(table => table.QrToken).HasMaxLength(255).IsRequired();
        builder.HasIndex(table => table.QrToken).IsUnique();
        builder.HasIndex(table => new { table.AreaId, table.TableNumber }).IsUnique();
    }
}

internal sealed class TableSessionConfiguration : IEntityTypeConfiguration<TableSession>
{
    public void Configure(EntityTypeBuilder<TableSession> builder)
    {
        builder.ToTable("table_sessions");
        builder.HasKey(session => session.Id);
        builder.Property(session => session.Status)
            .HasMaxLength(30)
            .HasDefaultValue(TableSessionStatuses.Open);
        builder.Property(session => session.OpenedAt)
            .HasColumnType("timestamp without time zone")
            .HasDefaultValueSql("now()");
        builder.Property(session => session.ClosedAt)
            .HasColumnType("timestamp without time zone");
        builder.HasMany(session => session.TableLinks)
            .WithOne(link => link.Session)
            .HasForeignKey(link => link.SessionId)
            .OnDelete(DeleteBehavior.NoAction);
    }
}

internal sealed class TableSessionTableConfiguration : IEntityTypeConfiguration<TableSessionTable>
{
    public void Configure(EntityTypeBuilder<TableSessionTable> builder)
    {
        builder.ToTable("table_session_tables");
        builder.HasKey(link => link.Id);
        builder.Property(link => link.JoinedAt)
            .HasColumnType("timestamp without time zone")
            .HasDefaultValueSql("now()");
        builder.Property(link => link.LeftAt)
            .HasColumnType("timestamp without time zone");
        builder.HasOne(link => link.Table)
            .WithMany(table => table.SessionLinks)
            .HasForeignKey(link => link.TableId)
            .OnDelete(DeleteBehavior.NoAction);
    }
}
