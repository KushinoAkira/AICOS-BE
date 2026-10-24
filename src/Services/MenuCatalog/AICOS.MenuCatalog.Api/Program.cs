using AICOS.BuildingBlocks.Hosting;
using AICOS.BuildingBlocks.Messaging;
using AICOS.BuildingBlocks.Persistence;
using AICOS.MenuCatalog.Api;
using AICOS.MenuCatalog.Api.Persistence;

var builder = WebApplication.CreateBuilder(args);

builder.AddAicosServiceDefaults(MenuCatalogService.Info);
builder.AddAicosPostgres<MenuCatalogDbContext>(MenuCatalogService.Info);
builder.AddAicosEventBus();

// builder.Services.AddIntegrationEventHandler<SomeEvent, SomeEventHandler>();   // consume events from other services
// builder.Services.AddAicosServiceClient<IOtherClient, OtherClient>(builder.Configuration, "Other");   // sync REST call to another service

var app = builder.Build();

await app.MigrateDatabaseAsync<MenuCatalogDbContext>();
app.UseAicosServiceDefaults();

var api = app.MapAicosApiGroup();
// api.MapMenuCatalogEndpoints();   // feature endpoints go here (see Features/)

await app.RunAsync();