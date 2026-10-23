using AICOS.BuildingBlocks.Hosting;
using AICOS.BuildingBlocks.Messaging;
using AICOS.BuildingBlocks.Persistence;
using AICOS.BuildingBlocks.Security;
using AICOS.Contracts.Events;
using AICOS.ReportingAnalytics.Api;
using AICOS.ReportingAnalytics.Api.Features.Orders;
using AICOS.ReportingAnalytics.Api.Features.Reports;
using AICOS.ReportingAnalytics.Api.Persistence;

var builder = WebApplication.CreateBuilder(args);

builder.AddAicosServiceDefaults(ReportingAnalyticsService.Info);
builder.AddAicosPostgres<ReportingAnalyticsDbContext>(ReportingAnalyticsService.Info);
builder.AddAicosEventBus();
builder.Services.AddAicosBranchDirectory(builder.Configuration);   // branch names, and the check that a branch belongs to the brand
builder.Services.AddSingleton<ReportClock>();

// The reports are built from the events of the order and payment services.
builder.Services.AddIntegrationEventHandler<OrderCreatedIntegrationEvent, ReportOrderCreatedHandler>();
builder.Services.AddIntegrationEventHandler<PaymentSucceededEvent, ReportPaymentSucceededHandler>();
builder.Services.AddIntegrationEventHandler<PaymentRefundedEvent, ReportPaymentRefundedHandler>();
builder.Services.AddIntegrationEventHandler<OrderCancelledIntegrationEvent, ReportOrderCancelledHandler>();
builder.Services.AddIntegrationEventHandler<OrderCompletedIntegrationEvent, ReportOrderCompletedHandler>();

var app = builder.Build();

await app.MigrateDatabaseAsync<ReportingAnalyticsDbContext>();
app.UseAicosServiceDefaults();

var api = app.MapAicosApiGroup();
api.MapReportEndpoints();

await app.RunAsync();
