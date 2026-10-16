using AICOS.BuildingBlocks.Hosting;
using AICOS.BuildingBlocks.Messaging;
using AICOS.BuildingBlocks.Persistence;
using AICOS.Ordering.Api;
using AICOS.Ordering.Api.BackgroundServices;
using AICOS.Ordering.Api.Domain.Interfaces;
using AICOS.Ordering.Api.Features;
using AICOS.Ordering.Api.Persistence;
using AICOS.Ordering.Api.Services;

var builder = WebApplication.CreateBuilder(args);

builder.AddAicosServiceDefaults(OrderingService.Info);
builder.AddAicosPostgres<OrderingDbContext>(OrderingService.Info);
builder.AddAicosEventBus();
// Dùng FakeEventBus trong RAM để test khi máy dev chưa bật Redis (Port 6379)
builder.Services.AddSingleton<IEventBus, FakeEventBus>();

// Đăng ký In-Memory FakeRepository để dùng tạm khi chưa có DB PostgreSQL
builder.Services.AddSingleton<IOrderRepository, FakeOrderRepository>();

// Đăng ký các Mock Service Client (MenuCatalog kiểm tra cờ IsAvailable, CustomerEngagement kiểm tra Voucher)
builder.Services.AddScoped<IMenuCatalogServiceClient, MockMenuCatalogServiceClient>();
builder.Services.AddScoped<ICustomerEngagementServiceClient, MockCustomerEngagementServiceClient>();

// Đăng ký Background Service tự động hủy đơn thanh toán online sau 5 phút (BR-09)
builder.Services.AddHostedService<OnlineOrderTimeoutWorker>();

var app = builder.Build();

try
{
    await app.MigrateDatabaseAsync<OrderingDbContext>();
}
catch (Exception ex)
{
    app.Logger.LogWarning("Chưa kết nối PostgreSQL ({Message}). Ứng dụng đang chạy ở chế độ In-Memory FakeRepository.", ex.Message);
}
app.UseAicosServiceDefaults();

var api = app.MapAicosApiGroup();
api.MapOrderingEndpoints(); // Đăng ký các endpoint cho POS: UC_POS_01 -> UC_POS_04

await app.RunAsync();