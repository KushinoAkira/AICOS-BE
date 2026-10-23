using AICOS.BuildingBlocks.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AICOS.TableReservation.Api.Persistence;

public sealed class TableReservationDbContext(DbContextOptions<TableReservationDbContext> options) : AicosDbContext(options)
{
    protected override string Schema => TableReservationService.Info.Schema;

    // public DbSet<Something> Somethings => Set<Something>();
}
