using AICOS.BuildingBlocks.Hosting;
using AICOS.BuildingBlocks.Messaging;
using AICOS.BuildingBlocks.Persistence;
using AICOS.TableReservation.Api;
using AICOS.TableReservation.Api.Persistence;

var builder = WebApplication.CreateBuilder(args);

builder.AddAicosServiceDefaults(TableReservationService.Info);
builder.AddAicosPostgres<TableReservationDbContext>(TableReservationService.Info);
builder.AddAicosEventBus();

// builder.Services.AddIntegrationEventHandler<SomeEvent, SomeEventHandler>();   // consume events from other services
// builder.Services.AddAicosServiceClient<IOtherClient, OtherClient>(builder.Configuration, "Other");   // sync REST call to another service

var app = builder.Build();

await app.MigrateDatabaseAsync<TableReservationDbContext>();
app.UseAicosServiceDefaults();

var api = app.MapAicosApiGroup();
// api.MapTableReservationEndpoints();   // feature endpoints go here (see Features/)

await app.RunAsync();