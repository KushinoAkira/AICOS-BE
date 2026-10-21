using AICOS.BuildingBlocks.Hosting;
using AICOS.BuildingBlocks.Messaging;
using AICOS.BuildingBlocks.Persistence;
using AICOS.KitchenOperations.Api;
using AICOS.KitchenOperations.Api.Domain.Interfaces;
using AICOS.KitchenOperations.Api.Features;
using AICOS.KitchenOperations.Api.Hubs;
using AICOS.KitchenOperations.Api.Persistence;
using AICOS.KitchenOperations.Api.Services;

var builder = WebApplication.CreateBuilder(args);

builder.AddAicosServiceDefaults(KitchenOperationsService.Info);
builder.AddAicosPostgres<KitchenOperationsDbContext>(KitchenOperationsService.Info);
builder.AddAicosEventBus();

// In-memory event bus only when Messaging:UseFakeEventBus=true (work without Redis); otherwise the Redis bus registered above is used.
if (builder.Configuration.GetValue<bool>("Messaging:UseFakeEventBus"))
{
    builder.Services.AddSingleton<IEventBus, FakeEventBus>();
}

// SignalR cho WebSocket Real-time KDS (NFR-28 < 300ms)
builder.Services.AddSignalR();
builder.Services.AddScoped<IKitchenNotifier, KitchenSignalRNotifier>();

// In-Memory Repository
builder.Services.AddSingleton<IKitchenTicketRepository, FakeKitchenTicketRepository>();

var app = builder.Build();

try
{
    await app.MigrateDatabaseAsync<KitchenOperationsDbContext>();
}
catch (Exception ex)
{
    app.Logger.LogWarning("Chưa kết nối PostgreSQL ({Message}). Ứng dụng chạy ở chế độ In-Memory FakeRepository.", ex.Message);
}

app.UseAicosServiceDefaults();

// Map SignalR Hub cho KDS và POS
app.MapHub<KitchenHub>("/hubs/kitchen");

var api = app.MapAicosApiGroup();
api.MapKitchenOperationsEndpoints(); // Đăng ký các endpoints UC_KITCHEN_01, UC_KITCHEN_02, UC_POS_07

await app.RunAsync();