using AICOS.BuildingBlocks.Hosting;
using AICOS.BuildingBlocks.Messaging;
using AICOS.BuildingBlocks.Persistence;
using AICOS.IdentityOrganization.Api;
using AICOS.Contracts.Events;
using AICOS.IdentityOrganization.Api.Features.Audit;
using AICOS.IdentityOrganization.Api.Features.Auth;
using AICOS.IdentityOrganization.Api.Features.Branches;
using AICOS.IdentityOrganization.Api.Features.Dev;
using AICOS.IdentityOrganization.Api.Features.Platform;
using AICOS.IdentityOrganization.Api.Features.Subscriptions;
using AICOS.IdentityOrganization.Api.Features.Tenants;
using AICOS.IdentityOrganization.Api.Features.Users;
using AICOS.IdentityOrganization.Api.Infrastructure;
using AICOS.IdentityOrganization.Api.Persistence;

var builder = WebApplication.CreateBuilder(args);

builder.AddAicosServiceDefaults(IdentityOrganizationService.Info);
builder.AddAicosPostgres<IdentityOrganizationDbContext>(IdentityOrganizationService.Info);
builder.AddAicosEventBus();

builder.Services.AddOptions<AuthOptions>()
    .Bind(builder.Configuration.GetSection(AuthOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddSingleton<PasswordService>();
builder.Services.AddScoped<AuthTokenIssuer>();
builder.Services.AddScoped<AuditLogger>();
builder.Services.AddOptions<SubscriptionOptions>()
    .Bind(builder.Configuration.GetSection(SubscriptionOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddScoped<SubscriptionReminderJob>();
builder.Services.AddHostedService<SubscriptionReminderService>();
builder.Services.AddIntegrationEventHandler<AuditRecordedEvent, AuditRecordedHandler>(); // other services: sensitive changes for the audit history
// Mail: a real SMTP server when Email:Smtp:Host and Email:FromAddress are set (see .env.example), otherwise the mails are only logged.
builder.Services.AddAicosEmail(builder.Configuration);
// Sign in with Google: needs Google:ClientId (environment variable Google__ClientId); the ID token is checked against Google's public keys.
builder.Services.Configure<GoogleAuthOptions>(builder.Configuration.GetSection(GoogleAuthOptions.SectionName));
builder.Services.AddMemoryCache();
builder.Services.AddHttpClient<IGoogleTokenVerifier, GoogleTokenVerifier>(c => c.Timeout = TimeSpan.FromSeconds(10));

var app = builder.Build();

await app.MigrateDatabaseAsync<IdentityOrganizationDbContext>();
await app.SeedIdentityAsync();
await app.SeedDemoPlansAsync();
app.UseAicosServiceDefaults();

var api = app.MapAicosApiGroup();
api.MapAuthEndpoints();
api.MapUserEndpoints();
api.MapBranchEndpoints();
api.MapTenantEndpoints();
api.MapBrandSelfServiceEndpoints();
api.MapAuditEndpoints();
api.MapPlanEndpoints();
api.MapTenantSubscriptionEndpoints();
api.MapMySubscriptionEndpoints();
api.MapPlatformEndpoints();

if (app.Environment.IsDevelopment())
{
    api.MapDevTokenEndpoints();
}

await app.RunAsync();
