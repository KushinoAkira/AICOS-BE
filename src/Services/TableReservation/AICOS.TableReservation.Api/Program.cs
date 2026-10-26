using AICOS.BuildingBlocks.Hosting;
using AICOS.BuildingBlocks.Http;
using AICOS.BuildingBlocks.Messaging;
using AICOS.BuildingBlocks.Persistence;
using AICOS.TableReservation.Api;
using AICOS.TableReservation.Api.Features.TableOperations;
using AICOS.TableReservation.Api.Integrations;
using AICOS.TableReservation.Api.Persistence;

var builder = WebApplication.CreateBuilder(args);

builder.AddAicosServiceDefaults(TableReservationService.Info);
builder.AddAicosPostgres<TableReservationDbContext>(TableReservationService.Info);
builder.AddAicosEventBus();
builder.Services.AddScoped<TableOperationsService>();
builder.Services.AddAicosServiceClient<IPaymentSettlementClient, PaymentSettlementClient>(
    builder.Configuration,
    "Payment");

// builder.Services.AddIntegrationEventHandler<SomeEvent, SomeEventHandler>();   // consume events from other services
// builder.Services.AddAicosServiceClient<IOtherClient, OtherClient>(builder.Configuration, "Other");   // sync REST call to another service

var app = builder.Build();

await app.MigrateDatabaseAsync<TableReservationDbContext>();
app.UseAicosServiceDefaults();

var api = app.MapAicosApiGroup();
api.MapTableOperationsEndpoints();

await app.RunAsync();
