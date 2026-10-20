using AICOS.BuildingBlocks.Hosting;
using AICOS.BuildingBlocks.Messaging;
using AICOS.BuildingBlocks.Persistence;
using AICOS.Payments.Api;
using AICOS.Payments.Api.Features.Providers;
using AICOS.Payments.Api.Infrastructure;
using AICOS.Payments.Api.Persistence;

var builder = WebApplication.CreateBuilder(args);

builder.AddAicosServiceDefaults(PaymentsService.Info);
builder.AddAicosPostgres<PaymentsDbContext>(PaymentsService.Info);
builder.AddAicosEventBus();

builder.Services.Configure<PaymentOptions>(builder.Configuration.GetSection(PaymentOptions.SectionName));
builder.Services.AddSingleton<SecretProtector>();

// builder.Services.AddIntegrationEventHandler<SomeEvent, SomeEventHandler>();   // consume events from other services
// builder.Services.AddAicosServiceClient<IOtherClient, OtherClient>(builder.Configuration, "Other");   // sync REST call to another service

var app = builder.Build();

await app.MigrateDatabaseAsync<PaymentsDbContext>();
await app.SeedPaymentsAsync();
app.UseAicosServiceDefaults();

var api = app.MapAicosApiGroup();
api.MapProviderEndpoints();

await app.RunAsync();
