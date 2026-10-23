using AICOS.BuildingBlocks.Hosting;
using AICOS.BuildingBlocks.Messaging;
using AICOS.BuildingBlocks.Persistence;
using AICOS.ReportingAnalytics.Api;
using AICOS.ReportingAnalytics.Api.Persistence;

var builder = WebApplication.CreateBuilder(args);

builder.AddAicosServiceDefaults(ReportingAnalyticsService.Info);
builder.AddAicosPostgres<ReportingAnalyticsDbContext>(ReportingAnalyticsService.Info);
builder.AddAicosEventBus();

// builder.Services.AddIntegrationEventHandler<SomeEvent, SomeEventHandler>();   // consume events from other services
// builder.Services.AddAicosServiceClient<IOtherClient, OtherClient>(builder.Configuration, "Other");   // sync REST call to another service

var app = builder.Build();

await app.MigrateDatabaseAsync<ReportingAnalyticsDbContext>();
app.UseAicosServiceDefaults();

var api = app.MapAicosApiGroup();
// api.MapReportingAnalyticsEndpoints();   // feature endpoints go here (see Features/)

await app.RunAsync();
