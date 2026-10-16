using AICOS.BuildingBlocks.Hosting;
using AICOS.BuildingBlocks.Messaging;
using AICOS.BuildingBlocks.Persistence;
using AICOS.WorkforceScheduling.Api;
using AICOS.WorkforceScheduling.Api.Persistence;

var builder = WebApplication.CreateBuilder(args);

builder.AddAicosServiceDefaults(WorkforceSchedulingService.Info);
builder.AddAicosPostgres<WorkforceSchedulingDbContext>(WorkforceSchedulingService.Info);
builder.AddAicosEventBus();

// builder.Services.AddIntegrationEventHandler<SomeEvent, SomeEventHandler>();   // consume events from other services
// builder.Services.AddAicosServiceClient<IOtherClient, OtherClient>(builder.Configuration, "Other");   // sync REST call to another service

var app = builder.Build();

await app.MigrateDatabaseAsync<WorkforceSchedulingDbContext>();
app.UseAicosServiceDefaults();

var api = app.MapAicosApiGroup();
// api.MapWorkforceSchedulingEndpoints();   // feature endpoints go here (see Features/)

await app.RunAsync();