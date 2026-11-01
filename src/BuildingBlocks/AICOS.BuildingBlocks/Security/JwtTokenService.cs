using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace AICOS.BuildingBlocks.Security;

/// <summary>Issues access tokens. Only the Identity service should call this; every service validates the result.</summary>
public sealed class JwtTokenService(IOptions<JwtOptions> options, TimeProvider clock)
{
    private readonly JwtOptions _options = options.Value;
    private readonly JwtSecurityTokenHandler _handler = new();

    public (string Token, DateTime ExpiresAtUtc) CreateAccessToken(Guid userId, string name, string role, Guid? branchId = null, Guid? tenantId = null, Guid? sessionId = null)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var expires = now.AddMinutes(_options.ExpiryMinutes);

        var claims = new List<Claim>
        {
            new(AicosClaimTypes.UserId, userId.ToString()),
            new(AicosClaimTypes.Name, name),
            new(AicosClaimTypes.Role, role),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
        };
        if (tenantId is not null)
        {
            claims.Add(new Claim(AicosClaimTypes.TenantId, tenantId.Value.ToString()));
        }

        if (branchId is not null)
        {
            claims.Add(new Claim(AicosClaimTypes.BranchId, branchId.Value.ToString()));
        }

        if (sessionId is not null)
        {
            claims.Add(new Claim(AicosClaimTypes.SessionId, sessionId.Value.ToString()));
        }

        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.SigningKey)),
            SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: _options.Issuer,
            audience: _options.Audience,
            claims: claims,
            notBefore: now,
            expires: expires,
            signingCredentials: credentials);

        return (_handler.WriteToken(token), expires);
    }
}
