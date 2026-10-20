using AICOS.BuildingBlocks.Hosting;
using AICOS.BuildingBlocks.Messaging;
using AICOS.BuildingBlocks.Persistence;
using AICOS.BuildingBlocks.Security;
using AICOS.Ordering.Api;
using AICOS.Ordering.Api.Features.PickupSlots;
using AICOS.Ordering.Api.Persistence;

var builder = WebApplication.CreateBuilder(args);

builder.AddAicosServiceDefaults(OrderingService.Info);
builder.AddAicosPostgres<OrderingDbContext>(OrderingService.Info);
builder.AddAicosEventBus();
builder.Services.AddAicosBranchDirectory(builder.Configuration);   // asks Identity whether a branch belongs to the brand

// builder.Services.AddIntegrationEventHandler<SomeEvent, SomeEventHandler>();   // consume events from other services
// builder.Services.AddAicosServiceClient<IOtherClient, OtherClient>(builder.Configuration, "Other");   // sync REST call to another service

var app = builder.Build();

await app.MigrateDatabaseAsync<OrderingDbContext>();
app.UseAicosServiceDefaults();

var api = app.MapAicosApiGroup();
api.MapPickupSlotEndpoints();

await app.RunAsync();