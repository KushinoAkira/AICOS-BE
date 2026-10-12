using AICOS.BuildingBlocks.Hosting;
using AICOS.BuildingBlocks.Messaging;
using AICOS.BuildingBlocks.Persistence;
using AICOS.WorkforceScheduling.Api;
using AICOS.WorkforceScheduling.Api.Features.Employees;
using AICOS.WorkforceScheduling.Api.Infrastructure;
using AICOS.WorkforceScheduling.Api.Persistence;

var builder = WebApplication.CreateBuilder(args);

builder.AddAicosServiceDefaults(WorkforceSchedulingService.Info);
builder.AddAicosPostgres<WorkforceSchedulingDbContext>(WorkforceSchedulingService.Info);
builder.AddAicosEventBus();

builder.Services.AddSingleton<ShopClock>();

var app = builder.Build();

await app.MigrateDatabaseAsync<WorkforceSchedulingDbContext>();
app.UseAicosServiceDefaults();

var api = app.MapAicosApiGroup();
api.MapEmployeeEndpoints();

await app.RunAsync();
