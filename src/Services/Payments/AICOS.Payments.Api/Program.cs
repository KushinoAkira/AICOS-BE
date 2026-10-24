using AICOS.BuildingBlocks.Hosting;
using AICOS.BuildingBlocks.Http;
using AICOS.BuildingBlocks.Messaging;
using AICOS.BuildingBlocks.Persistence;
using AICOS.Payments.Api;
using AICOS.Payments.Api.Features.Billing;
using AICOS.Payments.Api.Integrations;
using AICOS.Payments.Api.Persistence;

var builder = WebApplication.CreateBuilder(args);

builder.AddAicosServiceDefaults(PaymentsService.Info);
builder.AddAicosPostgres<PaymentsDbContext>(PaymentsService.Info);
builder.AddAicosEventBus();
builder.Services.AddScoped<BillingService>();
builder.Services.AddAicosServiceClient<IOrderBillingClient, OrderBillingClient>(
    builder.Configuration,
    "Order");

// builder.Services.AddIntegrationEventHandler<SomeEvent, SomeEventHandler>();   // consume events from other services
// builder.Services.AddAicosServiceClient<IOtherClient, OtherClient>(builder.Configuration, "Other");   // sync REST call to another service

var app = builder.Build();

await app.MigrateDatabaseAsync<PaymentsDbContext>();
app.UseAicosServiceDefaults();

var api = app.MapAicosApiGroup();
api.MapBillingEndpoints();

await app.RunAsync();
