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
using AICOS.KitchenOperations.Api.Features.KitchenAdmission;

var builder = WebApplication.CreateBuilder(args);

builder.AddAicosServiceDefaults(KitchenOperationsService.Info);
builder.AddAicosPostgres<KitchenOperationsDbContext>(KitchenOperationsService.Info);
builder.AddAicosEventBus();
builder.Services.AddAicosBranchDirectory(builder.Configuration);   // asks Identity whether a branch belongs to the brand

// Real-time screens for the bar and the cashiers (NFR-22). Several instances would need a Redis backplane for SignalR.
builder.Services.AddSignalR();
builder.Services.AddScoped<KitchenAdmissionService>();
// Every CupV1 adapter remains opt-in; no production policy defaults.
AICOS.KitchenOperations.Api.Features.CupServing.CupServingEndpoints.AddCupServing(builder);
if (builder.Configuration.GetValue<bool>("KitchenAdmission:ReceiverEnabled"))
{
    AdmissionMachineAuthentication.RequireKey(builder.Configuration);
    if (!builder.Configuration.GetValue<bool>("CupServing:AdmissionEnabled"))
        builder.Services.AddAdmissionMachineAuthentication(builder.Configuration);
}
builder.Services.AddScoped<IKitchenNotifier, KitchenSignalRNotifier>();

// The bar queue is fed by events from the order and payment services.
builder.Services.AddIntegrationEventHandler<OrderCreatedIntegrationEvent, OrderCreatedHandler>();
builder.Services.AddIntegrationEventHandler<TableTransferredIntegrationEvent, KitchenTableTransferHandler>();
builder.Services.AddIntegrationEventHandler<PaymentSucceededEvent, KitchenPaymentSucceededHandler>();
builder.Services.AddIntegrationEventHandler<TableBillRequestedIntegrationEvent, KitchenBillRequestedHandler>();
builder.Services.AddIntegrationEventHandler<OrderCancelledIntegrationEvent, KitchenOrderCancelledHandler>();
builder.Services.AddIntegrationEventHandler<OrderCompletedIntegrationEvent, KitchenOrderCompletedHandler>();
builder.Services.AddIntegrationEventHandler<OrderDeliveredIntegrationEvent, KitchenOrderDeliveredHandler>();

var app = builder.Build();

await app.MigrateDatabaseAsync<KitchenOperationsDbContext>();
app.UseAicosServiceDefaults();
app.MapKitchenAdmission();
AICOS.KitchenOperations.Api.Features.CupServing.CupServingEndpoints.MapCupServing(app);

app.MapHub<KitchenHub>("/hubs/kitchen");

var api = app.MapAicosApiGroup();
api.MapKitchenOperationsEndpoints();

await app.RunAsync();
