using AICOS.BuildingBlocks.Persistence;
using AICOS.InventoryPurchasing.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace AICOS.InventoryPurchasing.Api.Persistence;

public sealed class InventoryPurchasingDbContext(DbContextOptions<InventoryPurchasingDbContext> options) : AicosDbContext(options)
{
    protected override string Schema => InventoryPurchasingService.Info.Schema;

    public DbSet<Supplier> Suppliers => Set<Supplier>();

    public DbSet<Ingredient> Ingredients => Set<Ingredient>();

    public DbSet<StockTransaction> StockTransactions => Set<StockTransaction>();

    public DbSet<RecipeItem> RecipeItems => Set<RecipeItem>();

    public DbSet<PurchaseOrder> PurchaseOrders => Set<PurchaseOrder>();

    public DbSet<StockAdjustment> StockAdjustments => Set<StockAdjustment>();

    public DbSet<WasteReport> WasteReports => Set<WasteReport>();
}
