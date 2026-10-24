using AICOS.BuildingBlocks.Hosting;
using AICOS.BuildingBlocks.Http;
using AICOS.BuildingBlocks.Messaging;
using AICOS.BuildingBlocks.Persistence;
using AICOS.Ordering.Api;
using AICOS.Ordering.Api.Features.Orders;
using AICOS.Ordering.Api.Integrations;
using AICOS.Ordering.Api.Persistence;

var builder = WebApplication.CreateBuilder(args);

builder.AddAicosServiceDefaults(OrderingService.Info);
builder.AddAicosPostgres<OrderingDbContext>(OrderingService.Info);
builder.AddAicosEventBus();
builder.Services.AddAicosServiceClient<ITableSessionClient, TableSessionClient>(builder.Configuration, "Table");
builder.Services.AddAicosServiceClient<IMenuCatalogClient, MenuCatalogClient>(builder.Configuration, "Menu");
builder.Services.AddScoped<OrderApplicationService>();

// builder.Services.AddIntegrationEventHandler<SomeEvent, SomeEventHandler>();   // consume events from other services
// builder.Services.AddAicosServiceClient<IOtherClient, OtherClient>(builder.Configuration, "Other");   // sync REST call to another service

var app = builder.Build();

await app.MigrateDatabaseAsync<OrderingDbContext>();
app.UseAicosServiceDefaults();

var api = app.MapAicosApiGroup();
api.MapOrderEndpoints();

await app.RunAsync();
