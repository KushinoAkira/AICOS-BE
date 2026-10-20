using AICOS.BuildingBlocks.Caching;
using AICOS.BuildingBlocks.Hosting;
using AICOS.BuildingBlocks.Messaging;
using AICOS.BuildingBlocks.Persistence;
using AICOS.BuildingBlocks.Security;
using AICOS.MenuCatalog.Api;
using AICOS.MenuCatalog.Api.Features.Branches;
using AICOS.MenuCatalog.Api.Features.Categories;
using AICOS.MenuCatalog.Api.Features.Products;
using AICOS.MenuCatalog.Api.Features.PublicMenu;
using AICOS.MenuCatalog.Api.Infrastructure;
using AICOS.MenuCatalog.Api.Persistence;

var builder = WebApplication.CreateBuilder(args);

builder.AddAicosServiceDefaults(MenuCatalogService.Info);
builder.AddAicosPostgres<MenuCatalogDbContext>(MenuCatalogService.Info);
builder.AddAicosEventBus();

builder.Services.AddAicosCache();                                      // Redis cache of the published branch menus
builder.Services.AddAicosBranchDirectory(builder.Configuration);       // asks Identity whether a branch belongs to the brand
builder.Services.AddScoped<MenuCache>();
builder.Services.AddScoped<MenuChangeNotifier>();

var app = builder.Build();

await app.MigrateDatabaseAsync<MenuCatalogDbContext>();
app.UseAicosServiceDefaults();

var api = app.MapAicosApiGroup();
api.MapCategoryEndpoints();
api.MapProductEndpoints();
api.MapBranchMenuEndpoints();
api.MapPublicMenuEndpoints();

await app.RunAsync();
