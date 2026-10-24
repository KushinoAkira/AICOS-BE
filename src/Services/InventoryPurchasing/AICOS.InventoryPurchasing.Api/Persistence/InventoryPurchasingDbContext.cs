using AICOS.BuildingBlocks.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AICOS.InventoryPurchasing.Api.Persistence;

public sealed class InventoryPurchasingDbContext(DbContextOptions<InventoryPurchasingDbContext> options) : AicosDbContext(options)
{
    protected override string Schema => InventoryPurchasingService.Info.Schema;

    // public DbSet<Something> Somethings => Set<Something>();
}