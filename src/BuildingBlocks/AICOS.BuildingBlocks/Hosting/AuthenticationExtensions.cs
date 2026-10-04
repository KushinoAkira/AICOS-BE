using System.Text;
using AICOS.BuildingBlocks.Security;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace AICOS.BuildingBlocks.Hosting;

public static class AuthenticationExtensions
{
    /// <summary>
    /// JWT bearer authentication + the shared authorization policies. Every service (and the gateway) validates the same
    /// token, issued by the Identity service. Options are validated at start-up: a missing signing key fails fast.
    /// </summary>
    public static IServiceCollection AddAicosAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<JwtOptions>()
            .Bind(configuration.GetSection(JwtOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddSingleton<JwtTokenService>();

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<JwtOptions>>((bearer, jwtOptions) =>
            {
                var jwt = jwtOptions.Value;
                bearer.MapInboundClaims = false; // keep raw claim names: sub, role, branch_id
                bearer.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = jwt.Issuer,
                    ValidateAudience = true,
                    ValidAudience = jwt.Audience,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SigningKey)),
                    ClockSkew = TimeSpan.FromSeconds(30),
                    NameClaimType = AicosClaimTypes.Name,
                    RoleClaimType = AicosClaimTypes.Role,
                };
            });

        services.AddAuthorizationBuilder()
            .AddPolicy(AicosPolicies.AdminOnly, p => p.RequireRole(AicosRoles.SystemAdmin))
            .AddPolicy(AicosPolicies.Management, p => p.RequireRole(AicosRoles.SystemAdmin, AicosRoles.Manager))
            .AddPolicy(AicosPolicies.Staff, p => p.RequireRole(
                AicosRoles.SystemAdmin, AicosRoles.Manager, AicosRoles.Cashier, AicosRoles.WarehouseStaff));

        return services;
    }
}
