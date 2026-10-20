using AICOS.BuildingBlocks.Hosting;
using AICOS.BuildingBlocks.Messaging;
using AICOS.BuildingBlocks.Persistence;
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

// Events from other services.
builder.Services.AddIntegrationEventHandler<CustomerRegisteredEvent, CustomerRegisteredHandler>(); // Identity: new Customer account
builder.Services.AddIntegrationEventHandler<OrderCompletedEvent, OrderCompletedHandler>();         // Order: BR-25 loyalty points
builder.Services.AddIntegrationEventHandler<UserProfileUpdatedEvent, UserProfileUpdatedHandler>();   // Identity: name/phone edited (UC-03)

var app = builder.Build();

await app.MigrateDatabaseAsync<CustomerEngagementDbContext>();
app.UseAicosServiceDefaults();

var api = app.MapAicosApiGroup();
api.MapVoucherEndpoints();
api.MapCustomerEndpoints();

await app.RunAsync();
