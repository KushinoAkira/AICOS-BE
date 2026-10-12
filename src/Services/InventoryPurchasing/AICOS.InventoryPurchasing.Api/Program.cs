using AICOS.BuildingBlocks.Hosting;
using AICOS.BuildingBlocks.Http;
using AICOS.BuildingBlocks.Messaging;
using AICOS.BuildingBlocks.Persistence;
using AICOS.InventoryPurchasing.Api;
using AICOS.InventoryPurchasing.Api.Features.Ingredients;
using AICOS.InventoryPurchasing.Api.Features.PurchaseOrders;
using AICOS.InventoryPurchasing.Api.Features.Recipes;
using AICOS.InventoryPurchasing.Api.Features.StockCounts;
using AICOS.InventoryPurchasing.Api.Features.StockMovements;
using AICOS.InventoryPurchasing.Api.Features.Suppliers;
using AICOS.InventoryPurchasing.Api.Features.Waste;
using AICOS.InventoryPurchasing.Api.Infrastructure;
using AICOS.InventoryPurchasing.Api.Persistence;

var builder = WebApplication.CreateBuilder(args);

builder.AddAicosServiceDefaults(InventoryPurchasingService.Info);
builder.AddAicosPostgres<InventoryPurchasingDbContext>(InventoryPurchasingService.Info);
builder.AddAicosEventBus();

builder.Services.AddScoped<StockLedger>();

// Sync REST call to Menu Service (recipes must point at an existing product of the same branch).
builder.Services.AddAicosServiceClient<IMenuClient, MenuClient>(builder.Configuration, "Menu");

var app = builder.Build();

await app.MigrateDatabaseAsync<InventoryPurchasingDbContext>();
app.UseAicosServiceDefaults();

var api = app.MapAicosApiGroup();
api.MapSupplierEndpoints();
api.MapIngredientEndpoints();
api.MapRecipeEndpoints();
api.MapStockMovementEndpoints();
api.MapPurchaseOrderEndpoints();
api.MapStockAdjustmentEndpoints();
api.MapWasteReportEndpoints();

await app.RunAsync();
