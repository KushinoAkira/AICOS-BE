using AICOS.BuildingBlocks.Persistence;
using AICOS.TableReservation.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace AICOS.TableReservation.Api.Persistence;

public sealed class TableReservationDbContext(DbContextOptions<TableReservationDbContext> options) : AicosDbContext(options)
{
    protected override string Schema => TableReservationService.Info.Schema;

    public DbSet<TableArea> TableAreas => Set<TableArea>();
    public DbSet<CafeTable> CafeTables => Set<CafeTable>();
    public DbSet<TableSession> TableSessions => Set<TableSession>();
    public DbSet<TableSessionTable> TableSessionTables => Set<TableSessionTable>();
}
