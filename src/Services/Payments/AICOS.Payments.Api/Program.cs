using AICOS.BuildingBlocks.Hosting;
using AICOS.BuildingBlocks.Http;
using AICOS.Contracts.Events;
using AICOS.BuildingBlocks.Messaging;
using AICOS.BuildingBlocks.Persistence;
using AICOS.BuildingBlocks.Security;
using AICOS.Payments.Api;
using AICOS.Payments.Api.Domain;
using AICOS.Payments.Api.Features.Merchants;
using AICOS.Payments.Api.Features.Payments;
using AICOS.Payments.Api.Features.Policies;
using AICOS.Payments.Api.Features.Providers;
using AICOS.Payments.Api.Infrastructure;
using AICOS.Payments.Api.Persistence;

var builder = WebApplication.CreateBuilder(args);

builder.AddAicosServiceDefaults(PaymentsService.Info);
builder.AddAicosPostgres<PaymentsDbContext>(PaymentsService.Info);
builder.AddAicosEventBus();

builder.Services.Configure<PaymentOptions>(builder.Configuration.GetSection(PaymentOptions.SectionName));
builder.Services.AddSingleton<SecretProtector>();
builder.Services.Configure<PayOsOptions>(builder.Configuration.GetSection(PayOsOptions.SectionName));
builder.Services.AddHttpClient(PayOsGateway.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(15));
builder.Services.AddSingleton<PayOsGateway>();
builder.Services.Configure<MomoOptions>(builder.Configuration.GetSection(MomoOptions.SectionName));
builder.Services.PostConfigure<MomoOptions>(o => o.Live = o.Live || !string.IsNullOrWhiteSpace(o.PartnerCode));
builder.Services.AddHttpClient(MomoGateway.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(35));   // MoMo asks for at least 30 s
builder.Services.AddSingleton<MomoGateway>();
builder.Services.Configure<ZaloPayOptions>(builder.Configuration.GetSection(ZaloPayOptions.SectionName));
builder.Services.PostConfigure<ZaloPayOptions>(o => o.Live = o.Live || !string.IsNullOrWhiteSpace(o.AppId));
builder.Services.AddHttpClient(ZaloPayGateway.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddSingleton<ZaloPayGateway>();
// payOS talks to the real payOS API; the other providers are still simulated inside AICOS.
builder.Services.AddSingleton(sp => new GatewayRegistry(
    PaymentProvider.SupportedCodes.Where(c => c != PaymentProvider.PayOsCode && c != PaymentProvider.MomoCode && c != ZaloPay.Code).Select(c => (IPaymentGateway)new SimulatedGateway(c))
        .Append(sp.GetRequiredService<PayOsGateway>())
        .Append(sp.GetRequiredService<MomoGateway>())
        .Append(sp.GetRequiredService<ZaloPayGateway>())));
builder.Services.AddScoped<PaymentProcessor>();
builder.Services.AddScoped<RefundProcessor>();
builder.Services.AddHostedService<PaymentExpiryJob>();
builder.Services.AddAicosBranchDirectory(builder.Configuration);   // asks Identity whether a branch belongs to the brand
builder.Services.AddAicosServiceClient<IOrderDirectory, HttpOrderDirectory>(builder.Configuration, "Order");   // checks the order before taking money for it
builder.Services.AddIntegrationEventHandler<OrderCancelledIntegrationEvent, PaymentOrderCancelledHandler>();

// builder.Services.AddIntegrationEventHandler<SomeEvent, SomeEventHandler>();   // consume events from other services
// builder.Services.AddAicosServiceClient<IOtherClient, OtherClient>(builder.Configuration, "Other");   // sync REST call to another service

var app = builder.Build();

await app.MigrateDatabaseAsync<PaymentsDbContext>();
await app.SeedPaymentsAsync();
app.UseAicosServiceDefaults();

var api = app.MapAicosApiGroup();
api.MapProviderEndpoints();
api.MapCancellationPolicyEndpoints();
api.MapMerchantSettingEndpoints();
api.MapPaymentEndpoints();
api.MapOrderSettlementEndpoints();
api.MapWebhookEndpoints();
api.MapPayOsEndpoints();
api.MapMomoEndpoints();
api.MapZaloPayEndpoints();
api.MapRefundEndpoints();
api.MapReconciliationEndpoints();

await app.RunAsync();
