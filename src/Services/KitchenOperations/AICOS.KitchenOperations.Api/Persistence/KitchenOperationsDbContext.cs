using AICOS.BuildingBlocks.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AICOS.KitchenOperations.Api.Persistence;

public sealed class KitchenOperationsDbContext(DbContextOptions<KitchenOperationsDbContext> options) : AicosDbContext(options)
{
    protected override string Schema => KitchenOperationsService.Info.Schema;

    // public DbSet<Something> Somethings => Set<Something>();
}
