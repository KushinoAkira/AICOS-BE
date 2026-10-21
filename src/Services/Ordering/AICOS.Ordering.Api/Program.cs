using AICOS.BuildingBlocks.Hosting;
using AICOS.BuildingBlocks.Messaging;
using AICOS.BuildingBlocks.Persistence;
using AICOS.BuildingBlocks.Security;
using AICOS.Ordering.Api;
using AICOS.Ordering.Api.BackgroundServices;
using AICOS.Ordering.Api.Domain.Interfaces;
using AICOS.Ordering.Api.Features;
using AICOS.Ordering.Api.Features.PickupSlots;
using AICOS.Ordering.Api.Persistence;
using AICOS.Ordering.Api.Services;

var builder = WebApplication.CreateBuilder(args);

builder.AddAicosServiceDefaults(OrderingService.Info);
builder.AddAicosPostgres<OrderingDbContext>(OrderingService.Info);
builder.AddAicosEventBus();
builder.Services.AddAicosBranchDirectory(builder.Configuration);   // asks Identity whether a branch belongs to the brand

// The in-memory event bus is only for working without Redis; with Messaging:UseFakeEventBus=false (default) events really leave the service.
if (builder.Configuration.GetValue<bool>("Messaging:UseFakeEventBus"))
{
    builder.Services.AddSingleton<IEventBus, FakeEventBus>();
}

// POS and Guest QR ordering (SCRUM-66 to 117). These still run on in-memory repositories and mock clients: no tables, no tenant scoping yet.
builder.Services.AddSingleton<IOrderRepository, FakeOrderRepository>();
builder.Services.AddSingleton<IQrOrderingRepository, FakeQrOrderingRepository>();
builder.Services.AddSingleton<IRedisCartService, RedisCartService>();
builder.Services.AddScoped<IMenuCatalogServiceClient, MockMenuCatalogServiceClient>();
builder.Services.AddScoped<ICustomerEngagementServiceClient, MockCustomerEngagementServiceClient>();

// Cancels unpaid online orders after the timeout (BR-09 in the teammate's numbering).
builder.Services.AddHostedService<OnlineOrderTimeoutWorker>();

var app = builder.Build();

await app.MigrateDatabaseAsync<OrderingDbContext>();
app.UseAicosServiceDefaults();

var api = app.MapAicosApiGroup();
api.MapPickupSlotEndpoints();
api.MapOrderingEndpoints();   // UC_POS_01 to UC_POS_04
api.MapQrGuestEndpoints();    // Guest QR: SCRUM-66 to SCRUM-117

await app.RunAsync();
