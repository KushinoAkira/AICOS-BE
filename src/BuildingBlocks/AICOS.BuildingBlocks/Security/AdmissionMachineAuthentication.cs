using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

namespace AICOS.BuildingBlocks.Security;

/// <summary>Opt-in purpose-specific machine authentication; never changes the default Staff JWT scheme.</summary>
public static class AdmissionMachineAuthentication
{
    public const string Scheme = "KitchenAdmissionMachine";
    public const string Issuer = "aicos-ordering-admission";
    public const string Audience = "aicos-kitchen-admission";

    public static void AddAdmissionMachineAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddAuthentication().AddJwtBearer(Scheme, options =>
        {
            var key = RequireKey(configuration);
            options.MapInboundClaims = false;
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = Issuer,
                ValidateAudience = true,
                ValidAudience = Audience,
                ValidateLifetime = true,
                RequireExpirationTime = true,
                ClockSkew = TimeSpan.FromSeconds(5),
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)),
                ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
            };
            options.Events = new JwtBearerEvents
            {
                OnTokenValidated = context =>
                {
                    var principal = context.Principal!;
                    if (principal.FindFirst("client_id")?.Value != "ordering"
                        || !Guid.TryParse(principal.FindFirst("tenant_id")?.Value, out var tenant) || tenant == Guid.Empty
                        || !Guid.TryParse(principal.FindFirst("branch_id")?.Value, out var branch) || branch == Guid.Empty
                        || context.SecurityToken.ValidTo - context.SecurityToken.ValidFrom > TimeSpan.FromMinutes(2))
                        context.Fail("Invalid admission machine identity.");
                    return Task.CompletedTask;
                },
            };
        });
    }

    public static string RequireKey(IConfiguration configuration)
    {
        var key = configuration["KitchenAdmission:MachineKey"];
        if (string.IsNullOrWhiteSpace(key) || Encoding.UTF8.GetByteCount(key) < 32
            || key == configuration["Jwt:SigningKey"])
            throw new InvalidOperationException("A separate KitchenAdmission machine key of at least 32 bytes is required.");
        return key;
    }

    public static string Token(string key, Guid tenant, Guid branch, string scope, DateTime now)
    {
        var token = new JwtSecurityToken(Issuer, Audience,
            [new Claim("client_id", "ordering"), new Claim("tenant_id", tenant.ToString()),
             new Claim("branch_id", branch.ToString()), new Claim("scope", scope)],
            now, now.AddMinutes(1), new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)), SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
