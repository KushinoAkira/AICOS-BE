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
                bearer.Events = new JwtBearerEvents
                {
                    OnMessageReceived = context =>
                    {
                        context.Token = ExtractToken(context.Request.Headers.Authorization.ToString());
                        return Task.CompletedTask;
                    },
                    OnAuthenticationFailed = context =>
                    {
                        // The bearer handler only logs at Information (hidden by our Serilog overrides) - surface the reason.
                        context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>()
                            .CreateLogger("AICOS.Authentication")
                            .LogWarning("JWT rejected: {Reason}", context.Exception.Message);
                        return Task.CompletedTask;
                    },
                };
            });

        services.AddAuthorizationBuilder()
            .AddPolicy(AicosPolicies.PlatformAdmin, p => p.RequireRole(AicosRoles.SuperAdmin))
            .AddPolicy(AicosPolicies.AdminOnly, p => p.RequireRole(AicosRoles.BrandAdmin))
            .AddPolicy(AicosPolicies.Management, p => p.RequireRole(AicosRoles.BrandAdmin, AicosRoles.BranchManager))
            .AddPolicy(AicosPolicies.Staff, p => p.RequireRole(
                AicosRoles.BrandAdmin, AicosRoles.BranchManager, AicosRoles.Barista, AicosRoles.Cashier));

        return services;
    }

    /// <summary>
    /// Reads the raw JWT from an Authorization header, tolerating common copy-paste mistakes such as
    /// "Bearer Bearer xxx" (Swagger UI already adds "Bearer ") or a token wrapped in quotes. Null when no token.
    /// </summary>
    public static string? ExtractToken(string? authorizationHeader)
    {
        const string prefix = "Bearer ";
        var value = authorizationHeader?.Trim();
        if (string.IsNullOrEmpty(value) || !value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        while (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            value = value[prefix.Length..].Trim();
        }

        value = value.Trim('"', '\'', ' ');
        return value.Length == 0 ? null : value;
    }
}
