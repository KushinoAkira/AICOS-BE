using AICOS.BuildingBlocks.Hosting;
using AICOS.BuildingBlocks.Messaging;
using AICOS.BuildingBlocks.Persistence;
using AICOS.Ordering.Api;
using AICOS.Ordering.Api.BackgroundServices;
using AICOS.Ordering.Api.Domain.Interfaces;
using AICOS.Ordering.Api.Features;
using AICOS.Ordering.Api.Persistence;
using AICOS.Ordering.Api.Services;
using AICOS.Ordering.Api.Services.Takeaway;

var builder = WebApplication.CreateBuilder(args);

builder.AddAicosServiceDefaults(OrderingService.Info);
builder.AddAicosPostgres<OrderingDbContext>(OrderingService.Info);
builder.AddAicosEventBus();
// Dùng FakeEventBus trong RAM để test khi máy dev chưa bật Redis (Port 6379)
builder.Services.AddSingleton<IEventBus, FakeEventBus>();

// Đăng ký In-Memory FakeRepository để dùng làm fallback khi máy dev chưa kết nối PostgreSQL
builder.Services.AddSingleton<FakeOrderRepository>();
builder.Services.AddSingleton<FakeQrOrderingRepository>();

// Đăng ký Real PostgreSQL Repositories (tương tác trực tiếp với schema 34 bảng của teammate)
builder.Services.AddScoped<IOrderRepository, PostgresOrderRepository>();
builder.Services.AddScoped<IQrOrderingRepository, PostgresQrOrderingRepository>();

// Đăng ký Redis Cart Service cho Guest QR Ordering (SCRUM-66 -> SCRUM-117)
builder.Services.AddSingleton<IRedisCartService, RedisCartService>();

// Đăng ký các Mock Service Client (MenuCatalog kiểm tra cờ IsAvailable, CustomerEngagement kiểm tra Voucher)
builder.Services.AddScoped<IMenuCatalogServiceClient, MockMenuCatalogServiceClient>();
builder.Services.AddScoped<ICustomerEngagementServiceClient, MockCustomerEngagementServiceClient>();

builder.Services.AddScoped<PickupOrderService>();
builder.Services.AddScoped<CustomerProfileService>();

// Đăng ký Background Service tự động hủy đơn thanh toán online sau 5 phút (BR-09)
builder.Services.AddHostedService<OnlineOrderTimeoutWorker>();
builder.Services.AddHostedService<TakeawayExpiryWorker>();
builder.Services.AddHostedService<CustomerMaintenanceWorker>();

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
api.MapQrGuestEndpoints();   // Đăng ký các endpoint cho Guest QR: SCRUM-66 -> SCRUM-117
api.MapPickupEndpoints();    // SCRUM-79 -> 82, 119-121
api.MapCustomerProfileEndpoints(); // SCRUM-78, 81, 83, 118, 122, 123

await app.RunAsync();