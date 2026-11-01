using AICOS.CustomerEngagement.Api.Features.Notifications;
using AICOS.BuildingBlocks.Hosting;
using AICOS.BuildingBlocks.Messaging;
using AICOS.BuildingBlocks.Persistence;
using AICOS.BuildingBlocks.Security;
using AICOS.Contracts.Events;
using AICOS.CustomerEngagement.Api;
using AICOS.CustomerEngagement.Api.Features.Customers;
using AICOS.CustomerEngagement.Api.Features.Loyalty;
using AICOS.CustomerEngagement.Api.Features.Vouchers;
using AICOS.CustomerEngagement.Api.Persistence;

var builder = WebApplication.CreateBuilder(args);

builder.AddAicosServiceDefaults(CustomerEngagementService.Info);
builder.AddAicosPostgres<CustomerEngagementDbContext>(CustomerEngagementService.Info);
builder.AddAicosEventBus();

builder.Services.Configure<LoyaltyOptions>(builder.Configuration.GetSection("Loyalty"));
builder.Services.AddScoped<LoyaltyRulesService>();
builder.Services.AddAicosBranchDirectory(builder.Configuration);   // asks Identity whether a branch belongs to the brand

// Events from other services.
builder.Services.AddIntegrationEventHandler<CustomerRegisteredEvent, CustomerRegisteredHandler>(); // Identity: new Customer account
builder.Services.AddIntegrationEventHandler<OrderPaidIntegrationEvent, OrderPaidHandler>();                    // Order: BR-25 points after payment
builder.Services.AddIntegrationEventHandler<OrderCancelledIntegrationEvent, OrderCancelledLoyaltyHandler>();      // Order: voucher back / points taken back
builder.Services.AddIntegrationEventHandler<CustomerDataErasedEvent, CustomerDataErasedHandler>();   // Identity: UC-66 personal data deleted
builder.Services.AddIntegrationEventHandler<UserProfileUpdatedEvent, UserProfileUpdatedHandler>();   // Identity: name/phone edited (UC-03)
builder.Services.AddIntegrationEventHandler<OrderCreatedIntegrationEvent, StaffNotificationHandler>();
builder.Services.AddIntegrationEventHandler<OrderPaidIntegrationEvent, StaffNotificationHandler>();
builder.Services.AddIntegrationEventHandler<KitchenOrderReadyIntegrationEvent, StaffNotificationHandler>();
builder.Services.AddIntegrationEventHandler<KitchenOrderRemakeIntegrationEvent, StaffNotificationHandler>();
builder.Services.AddIntegrationEventHandler<TableBillRequestedIntegrationEvent, StaffNotificationHandler>();
builder.Services.AddIntegrationEventHandler<PaymentSucceededEvent, StaffNotificationHandler>();
builder.Services.AddIntegrationEventHandler<RefundRequestedEvent, StaffNotificationHandler>();
builder.Services.AddIntegrationEventHandler<PaymentRefundFailedEvent, StaffNotificationHandler>();
builder.Services.AddIntegrationEventHandler<CashClosingSubmittedEvent, StaffNotificationHandler>();
builder.Services.AddIntegrationEventHandler<ReplacementShiftOpenedEvent, StaffNotificationHandler>();
builder.Services.AddIntegrationEventHandler<ReplacementRegisteredEvent, StaffNotificationHandler>();
builder.Services.AddIntegrationEventHandler<SubscriptionRenewalDueEvent, StaffNotificationHandler>();
builder.Services.AddIntegrationEventHandler<SubscriptionOverdueEvent, StaffNotificationHandler>();
builder.Services.AddIntegrationEventHandler<BrandRegisteredEvent, StaffNotificationHandler>();
builder.Services.AddIntegrationEventHandler<OrderStatusChangedIntegrationEvent, OrderStatusNotificationHandler>();   // Order: a notification for the member

var app = builder.Build();

await app.MigrateDatabaseAsync<CustomerEngagementDbContext>();
app.UseAicosServiceDefaults();

var api = app.MapAicosApiGroup();
api.MapVoucherEndpoints();
api.MapCustomerEndpoints();
api.MapRedemptionEndpoints();
api.MapLoyaltyRuleEndpoints();
api.MapNotificationEndpoints();
api.MapStaffNotificationEndpoints();

await app.RunAsync();
