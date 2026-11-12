using AICOS.BuildingBlocks.Caching;
using AICOS.BuildingBlocks.Hosting;
using AICOS.BuildingBlocks.Http;
using AICOS.BuildingBlocks.Messaging;
using AICOS.BuildingBlocks.Persistence;
using AICOS.BuildingBlocks.Security;
using AICOS.Contracts.Events;
using AICOS.Ordering.Api;
using AICOS.Ordering.Api.Features;
using AICOS.Ordering.Api.Features.Orders;
using AICOS.Ordering.Api.Features.PickupSlots;
using AICOS.Ordering.Api.Features.Qr;
using AICOS.Ordering.Api.Persistence;
using AICOS.Ordering.Api.Services;
using AICOS.Ordering.Api.Features.KitchenAdmission;

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
builder.AddKitchenAdmission();
AICOS.Ordering.Api.Features.Fulfillment.FulfillmentEndpoints.AddFulfillment(builder);
builder.Services.AddScoped<OrderEventPublisher>();

// Real-time channel for customers (a guest at a table and signed-in members) in place of polling. Several instances would need a Redis backplane.
builder.Services.AddSignalR();
builder.Services.AddScoped<IOrderNotifier, SignalROrderNotifier>();
builder.Services.AddHostedService<PickupOrderExpiryWorker>();
builder.Services.AddHostedService<TableSessionExpiryWorker>();

// QR table ordering: the shared cart lives in Redis, sessions and orders in Postgres.
builder.Services.AddAicosCache();
builder.Services.AddSingleton<GuestSessionTokens>();
builder.Services.AddScoped<QrCartStore>();
builder.Services.AddAicosServiceClient<ITableQrClient, HttpTableQrClient>(builder.Configuration, "Table");

// What the other services tell us.
builder.Services.AddIntegrationEventHandler<PaymentSucceededEvent, PaymentSucceededHandler>();
builder.Services.AddIntegrationEventHandler<PaymentFailedEvent, PaymentFailedHandler>();
builder.Services.AddIntegrationEventHandler<PaymentRefundedEvent, PaymentRefundedHandler>();
builder.Services.AddIntegrationEventHandler<PaymentRefundFailedEvent, PaymentRefundFailedHandler>();
builder.Services.AddIntegrationEventHandler<KitchenOrderStartedIntegrationEvent, KitchenOrderStartedHandler>();
builder.Services.AddIntegrationEventHandler<KitchenOrderReadyIntegrationEvent, KitchenOrderReadyHandler>();
builder.Services.AddIntegrationEventHandler<KitchenOrderRemakeIntegrationEvent, KitchenOrderRemakeHandler>();


var app = builder.Build();

await app.MigrateDatabaseAsync<OrderingDbContext>();
app.UseAicosServiceDefaults();
AICOS.Ordering.Api.Features.Fulfillment.FulfillmentEndpoints.MapFulfillment(app);

var api = app.MapAicosApiGroup();
api.MapPickupSlotEndpoints();
api.MapPickupOrderEndpoints();
api.MapOrderingEndpoints();
api.MapTableTransferEndpoints();
api.MapQrOrderEndpoints();

app.MapHub<OrderTrackingHub>("/hubs/orders");

await app.RunAsync();
