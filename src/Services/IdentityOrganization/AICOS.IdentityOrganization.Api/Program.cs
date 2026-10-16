using AICOS.BuildingBlocks.Hosting;
using AICOS.BuildingBlocks.Messaging;
using AICOS.BuildingBlocks.Persistence;
using AICOS.IdentityOrganization.Api;
using AICOS.IdentityOrganization.Api.Persistence;

var builder = WebApplication.CreateBuilder(args);

builder.AddAicosServiceDefaults(IdentityOrganizationService.Info);
builder.AddAicosPostgres<IdentityOrganizationDbContext>(IdentityOrganizationService.Info);
builder.AddAicosEventBus();

// builder.Services.AddIntegrationEventHandler<SomeEvent, SomeEventHandler>();   // consume events from other services
// builder.Services.AddAicosServiceClient<IOtherClient, OtherClient>(builder.Configuration, "Other");   // sync REST call to another service

var app = builder.Build();

await app.MigrateDatabaseAsync<IdentityOrganizationDbContext>();
app.UseAicosServiceDefaults();

var api = app.MapAicosApiGroup();
// api.MapIdentityOrganizationEndpoints();   // feature endpoints go here (see Features/)

await app.RunAsync();