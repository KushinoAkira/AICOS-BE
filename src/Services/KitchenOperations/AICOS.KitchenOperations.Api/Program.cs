using AICOS.BuildingBlocks.Hosting;
using AICOS.BuildingBlocks.Messaging;
using AICOS.BuildingBlocks.Persistence;
using AICOS.BuildingBlocks.Security;
using AICOS.Contracts.Events;
using AICOS.KitchenOperations.Api;
using AICOS.KitchenOperations.Api.Features;
using AICOS.KitchenOperations.Api.Hubs;
using AICOS.KitchenOperations.Api.Persistence;
using AICOS.KitchenOperations.Api.Services;

var builder = WebApplication.CreateBuilder(args);

builder.AddAicosServiceDefaults(KitchenOperationsService.Info);
builder.AddAicosPostgres<KitchenOperationsDbContext>(KitchenOperationsService.Info);
builder.AddAicosEventBus();
builder.Services.AddAicosBranchDirectory(builder.Configuration);   // asks Identity whether a branch belongs to the brand

// Real-time screens for the bar and the cashiers (NFR-22). Several instances would need a Redis backplane for SignalR.
builder.Services.AddSignalR();
builder.Services.AddScoped<IKitchenNotifier, KitchenSignalRNotifier>();

// The bar queue is fed by events from the order and payment services.
builder.Services.AddIntegrationEventHandler<OrderCreatedIntegrationEvent, OrderCreatedHandler>();
builder.Services.AddIntegrationEventHandler<PaymentSucceededEvent, KitchenPaymentSucceededHandler>();
builder.Services.AddIntegrationEventHandler<TableBillRequestedIntegrationEvent, KitchenBillRequestedHandler>();
builder.Services.AddIntegrationEventHandler<OrderCancelledIntegrationEvent, KitchenOrderCancelledHandler>();
builder.Services.AddIntegrationEventHandler<OrderCompletedIntegrationEvent, KitchenOrderCompletedHandler>();

var app = builder.Build();

await app.MigrateDatabaseAsync<KitchenOperationsDbContext>();
app.UseAicosServiceDefaults();

app.MapHub<KitchenHub>("/hubs/kitchen");

var api = app.MapAicosApiGroup();
api.MapKitchenOperationsEndpoints();

await app.RunAsync();
