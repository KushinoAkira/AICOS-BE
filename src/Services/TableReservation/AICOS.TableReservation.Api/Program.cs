using AICOS.BuildingBlocks.Hosting;
using AICOS.BuildingBlocks.Http;
using AICOS.BuildingBlocks.Messaging;
using AICOS.BuildingBlocks.Persistence;
using AICOS.BuildingBlocks.Security;
using AICOS.TableReservation.Api;
using AICOS.TableReservation.Api.Features.Areas;
using AICOS.TableReservation.Api.Features.DiningTables;
using AICOS.TableReservation.Api.Persistence;

var builder = WebApplication.CreateBuilder(args);

builder.AddAicosServiceDefaults(TableReservationService.Info);
builder.AddAicosPostgres<TableReservationDbContext>(TableReservationService.Info);
builder.AddAicosEventBus();
builder.Services.AddAicosBranchDirectory(builder.Configuration);   // asks Identity whether a branch belongs to the brand
builder.Services.AddAicosServiceClient<IPlanLimitsClient, HttpPlanLimitsClient>(builder.Configuration, "Identity");   // and how many tables the plan allows

var app = builder.Build();

await app.MigrateDatabaseAsync<TableReservationDbContext>();
app.UseAicosServiceDefaults();

var api = app.MapAicosApiGroup();
api.MapAreaEndpoints();
api.MapDiningTableEndpoints();
api.MapTableBatchEndpoints();

await app.RunAsync();
