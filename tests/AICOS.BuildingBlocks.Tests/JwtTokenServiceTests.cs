using System.IdentityModel.Tokens.Jwt;
using AICOS.BuildingBlocks.Security;
using Microsoft.Extensions.Options;

namespace AICOS.BuildingBlocks.Tests;

public class JwtTokenServiceTests
{
    private static readonly JwtOptions Options = new()
    {
        Issuer = "aicos-test",
        Audience = "aicos-test-clients",
        SigningKey = "unit-test-signing-key-0123456789-abcdef",
        ExpiryMinutes = 30,
    };

    [Fact]
    public void CreateAccessToken_contains_identity_role_and_branch_claims()
    {
        var userId = Guid.NewGuid();
        var branchId = Guid.NewGuid();
        var service = new JwtTokenService(Microsoft.Extensions.Options.Options.Create(Options), TimeProvider.System);

        var (token, expiresAt) = service.CreateAccessToken(userId, "Cashier A", AicosRoles.Cashier, branchId);

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);
        jwt.Issuer.Should().Be("aicos-test");
        jwt.Audiences.Should().ContainSingle().Which.Should().Be("aicos-test-clients");
        jwt.Claims.Single(c => c.Type == AicosClaimTypes.UserId).Value.Should().Be(userId.ToString());
        jwt.Claims.Single(c => c.Type == AicosClaimTypes.Role).Value.Should().Be(AicosRoles.Cashier);
        jwt.Claims.Single(c => c.Type == AicosClaimTypes.BranchId).Value.Should().Be(branchId.ToString());
        expiresAt.Should().BeCloseTo(DateTime.UtcNow.AddMinutes(30), TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void CreateAccessToken_omits_branch_claim_for_chain_wide_users()
    {
        var service = new JwtTokenService(Microsoft.Extensions.Options.Options.Create(Options), TimeProvider.System);

        var (token, _) = service.CreateAccessToken(Guid.NewGuid(), "Admin", AicosRoles.BrandAdmin);

        new JwtSecurityTokenHandler().ReadJwtToken(token).Claims
            .Should().NotContain(c => c.Type == AicosClaimTypes.BranchId);
    }

    [Fact]
    public void All_seven_roles_from_the_proposal_are_defined()
    {
        AicosRoles.All.Should().HaveCount(7).And.OnlyHaveUniqueItems();
    }
}
