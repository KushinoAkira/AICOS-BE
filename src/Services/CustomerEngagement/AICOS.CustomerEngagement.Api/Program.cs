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
builder.Services.AddIntegrationEventHandler<UserProfileUpdatedEvent, UserProfileUpdatedHandler>();   // Identity: name/phone edited (UC-03)

var app = builder.Build();

await app.MigrateDatabaseAsync<CustomerEngagementDbContext>();
app.UseAicosServiceDefaults();

var api = app.MapAicosApiGroup();
api.MapVoucherEndpoints();
api.MapCustomerEndpoints();
api.MapRedemptionEndpoints();
api.MapLoyaltyRuleEndpoints();

await app.RunAsync();
