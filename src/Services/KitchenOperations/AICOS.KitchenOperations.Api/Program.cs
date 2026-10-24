using AICOS.BuildingBlocks.Hosting;
using AICOS.BuildingBlocks.Messaging;
using AICOS.BuildingBlocks.Persistence;
using AICOS.KitchenOperations.Api;
using AICOS.KitchenOperations.Api.Features.Preparation;
using AICOS.KitchenOperations.Api.Persistence;

var builder = WebApplication.CreateBuilder(args);

builder.AddAicosServiceDefaults(KitchenOperationsService.Info);
builder.AddAicosPostgres<KitchenOperationsDbContext>(KitchenOperationsService.Info);
builder.AddAicosEventBus();
builder.Services.AddScoped<PreparationService>();

// builder.Services.AddIntegrationEventHandler<SomeEvent, SomeEventHandler>();   // consume events from other services
// builder.Services.AddAicosServiceClient<IOtherClient, OtherClient>(builder.Configuration, "Other");   // sync REST call to another service

var app = builder.Build();

await app.MigrateDatabaseAsync<KitchenOperationsDbContext>();
app.UseAicosServiceDefaults();

var api = app.MapAicosApiGroup();
api.MapPreparationEndpoints();

await app.RunAsync();
