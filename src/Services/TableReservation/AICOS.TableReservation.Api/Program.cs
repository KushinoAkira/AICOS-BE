using AICOS.BuildingBlocks.Hosting;
using AICOS.BuildingBlocks.Messaging;
using AICOS.BuildingBlocks.Persistence;
using AICOS.TableReservation.Api;
using AICOS.TableReservation.Api.Features.DiningTables;
using AICOS.TableReservation.Api.Persistence;

var builder = WebApplication.CreateBuilder(args);

builder.AddAicosServiceDefaults(TableReservationService.Info);
builder.AddAicosPostgres<TableReservationDbContext>(TableReservationService.Info);
builder.AddAicosEventBus();

var app = builder.Build();

await app.MigrateDatabaseAsync<TableReservationDbContext>();
app.UseAicosServiceDefaults();

var api = app.MapAicosApiGroup();
api.MapDiningTableEndpoints();

await app.RunAsync();
