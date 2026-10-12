using AICOS.BuildingBlocks.Hosting;
using AICOS.BuildingBlocks.Messaging;
using AICOS.BuildingBlocks.Persistence;
using AICOS.IdentityOrganization.Api;
using AICOS.IdentityOrganization.Api.Features.Auth;
using AICOS.IdentityOrganization.Api.Features.Branches;
using AICOS.IdentityOrganization.Api.Features.Dev;
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
builder.Services.AddSingleton<IEmailSender, LoggingEmailSender>();

var app = builder.Build();

await app.MigrateDatabaseAsync<IdentityOrganizationDbContext>();
await app.SeedIdentityAsync();
app.UseAicosServiceDefaults();

var api = app.MapAicosApiGroup();
api.MapAuthEndpoints();
api.MapUserEndpoints();
api.MapBranchEndpoints();

if (app.Environment.IsDevelopment())
{
    api.MapDevTokenEndpoints();
}

await app.RunAsync();
