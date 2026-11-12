using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace AICOS.BuildingBlocks.Security;

public static class FulfillmentMachineAuthentication
{
    public const string Scheme = "CupFulfillmentMachine";
    public static string Key(IConfiguration config)
    {
        var key = config["CupServing:MachineKey"];
        if (string.IsNullOrWhiteSpace(key) || Encoding.UTF8.GetByteCount(key) < 32
            || key == config["Jwt:SigningKey"] || key == config["KitchenAdmission:MachineKey"])
            throw new InvalidOperationException("Cup fulfillment requires a separate machine key of at least 32 bytes.");
        return key;
    }

    public static void Register(IServiceCollection services, IConfiguration config)
    {
        var key = Key(config);
        services.AddAuthentication().AddJwtBearer(Scheme, options =>
        {
            options.MapInboundClaims = false;
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = "aicos-kitchen-fulfillment",
                ValidateAudience = true,
                ValidAudience = "aicos-ordering-fulfillment",
                ValidateLifetime = true,
                RequireExpirationTime = true,
                ClockSkew = TimeSpan.FromSeconds(5),
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)),
                ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
            };
            options.Events = new Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerEvents
            {
                OnTokenValidated = context =>
                {
                    if (context.Principal?.FindFirst("client_id")?.Value != "kitchen"
                        || context.SecurityToken.ValidTo - context.SecurityToken.ValidFrom > TimeSpan.FromMinutes(2))
                        context.Fail("Invalid fulfillment machine identity.");
                    return Task.CompletedTask;
                },
            };
        });
    }

    public static string Token(string key, Guid tenant, Guid branch) => new JwtSecurityTokenHandler().WriteToken(
        new JwtSecurityToken("aicos-kitchen-fulfillment", "aicos-ordering-fulfillment",
            [new Claim("client_id", "kitchen"), new Claim("tenant_id", tenant.ToString()),
                new Claim("branch_id", branch.ToString()), new Claim("scope", "ordering.fulfillment.write")],
            DateTime.UtcNow, DateTime.UtcNow.AddMinutes(1),
            new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)), SecurityAlgorithms.HmacSha256)));
}
