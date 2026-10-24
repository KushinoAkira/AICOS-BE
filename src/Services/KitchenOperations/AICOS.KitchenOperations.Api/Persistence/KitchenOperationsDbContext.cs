using AICOS.BuildingBlocks.Persistence;
using AICOS.KitchenOperations.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace AICOS.KitchenOperations.Api.Persistence;

public sealed class KitchenOperationsDbContext(
    DbContextOptions<KitchenOperationsDbContext> options)
    : AicosDbContext(options)
{
    protected override string Schema => KitchenOperationsService.Info.Schema;

    public DbSet<PreparationOrder> Orders => Set<PreparationOrder>();
    public DbSet<PreparationItem> Items => Set<PreparationItem>();
    public DbSet<PreparationEvent> Events => Set<PreparationEvent>();
}
