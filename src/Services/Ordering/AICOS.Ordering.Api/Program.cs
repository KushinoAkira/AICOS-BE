using AICOS.BuildingBlocks.Hosting;
using AICOS.BuildingBlocks.Http;
using AICOS.BuildingBlocks.Messaging;
using AICOS.BuildingBlocks.Persistence;
using AICOS.BuildingBlocks.Security;
using AICOS.Contracts.Events;
using AICOS.Ordering.Api;
using AICOS.Ordering.Api.Domain.Interfaces;
using AICOS.Ordering.Api.Features;
using AICOS.Ordering.Api.Features.Orders;
using AICOS.Ordering.Api.Features.PickupSlots;
using AICOS.Ordering.Api.Persistence;
using AICOS.Ordering.Api.Services;

var builder = WebApplication.CreateBuilder(args);

builder.AddAicosServiceDefaults(OrderingService.Info);
builder.AddAicosPostgres<OrderingDbContext>(OrderingService.Info);
builder.AddAicosEventBus();
builder.Services.AddAicosBranchDirectory(builder.Configuration);   // asks Identity whether a branch belongs to the brand

// Other services. Prices come from Menu, vouchers from Customer, refunds go to Payment; the caller's bearer token is forwarded.
builder.Services.AddAicosServiceClient<IMenuCatalogClient, HttpMenuCatalogClient>(builder.Configuration, "Menu");
builder.Services.AddAicosServiceClient<IVoucherClient, HttpVoucherClient>(builder.Configuration, "Customer");
builder.Services.AddAicosServiceClient<IPaymentClient, HttpPaymentClient>(builder.Configuration, "Payment");

builder.Services.AddSingleton<ShopTime>();
builder.Services.AddScoped<OrderEventPublisher>();
builder.Services.AddHostedService<PickupOrderExpiryWorker>();

// What the other services tell us.
builder.Services.AddIntegrationEventHandler<PaymentSucceededEvent, PaymentSucceededHandler>();
builder.Services.AddIntegrationEventHandler<PaymentFailedEvent, PaymentFailedHandler>();
builder.Services.AddIntegrationEventHandler<PaymentRefundedEvent, PaymentRefundedHandler>();
builder.Services.AddIntegrationEventHandler<PaymentRefundFailedEvent, PaymentRefundFailedHandler>();
builder.Services.AddIntegrationEventHandler<KitchenOrderStartedIntegrationEvent, KitchenOrderStartedHandler>();
builder.Services.AddIntegrationEventHandler<KitchenOrderReadyIntegrationEvent, KitchenOrderReadyHandler>();

// Guest QR ordering (SCRUM-66 to 117, another team member). Still on in-memory repositories: no tables, no tenant scoping, not for production.
builder.Services.AddSingleton<IQrOrderingRepository, FakeQrOrderingRepository>();
builder.Services.AddSingleton<IRedisCartService, RedisCartService>();

var app = builder.Build();

await app.MigrateDatabaseAsync<OrderingDbContext>();
app.UseAicosServiceDefaults();

var api = app.MapAicosApiGroup();
api.MapPickupSlotEndpoints();
api.MapPickupOrderEndpoints();
api.MapOrderingEndpoints();
api.MapQrGuestEndpoints();

await app.RunAsync();
