using AICOS.BuildingBlocks.Hosting;
using AICOS.BuildingBlocks.Messaging;
using AICOS.BuildingBlocks.Persistence;
using AICOS.Payments.Api;
using AICOS.Payments.Api.Persistence;

var builder = WebApplication.CreateBuilder(args);

builder.AddAicosServiceDefaults(PaymentsService.Info);
builder.AddAicosPostgres<PaymentsDbContext>(PaymentsService.Info);
builder.AddAicosEventBus();

// builder.Services.AddIntegrationEventHandler<SomeEvent, SomeEventHandler>();   // consume events from other services
// builder.Services.AddAicosServiceClient<IOtherClient, OtherClient>(builder.Configuration, "Other");   // sync REST call to another service

var app = builder.Build();

await app.MigrateDatabaseAsync<PaymentsDbContext>();
app.UseAicosServiceDefaults();

var api = app.MapAicosApiGroup();
// api.MapPaymentsEndpoints();   // feature endpoints go here (see Features/)

await app.RunAsync();