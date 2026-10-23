using AICOS.BuildingBlocks.Hosting;
using AICOS.BuildingBlocks.Messaging;
using AICOS.BuildingBlocks.Persistence;
using AICOS.CustomerEngagement.Api;
using AICOS.Ordering.Api.Features;
using AICOS.Ordering.Api.Persistence;
using AICOS.Ordering.Api.Services.Takeaway;

var builder = WebApplication.CreateBuilder(args);

builder.AddAicosServiceDefaults(CustomerEngagementService.Info);
builder.AddAicosPostgres<OrderingDbContext>(CustomerEngagementService.Info);
builder.AddAicosEventBus();
builder.Services.AddScoped<CustomerProfileService>();

var app = builder.Build();

try
{
    await app.MigrateDatabaseAsync<OrderingDbContext>();
}
catch (Exception ex)
{
    app.Logger.LogWarning("Chưa kết nối PostgreSQL ({Message}).", ex.Message);
}

app.UseAicosServiceDefaults();

var api = app.MapAicosApiGroup();
api.MapCustomerProfileEndpoints();

await app.RunAsync();
