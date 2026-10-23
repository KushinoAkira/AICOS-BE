using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AICOS.BuildingBlocks.Exceptions;
using AICOS.BuildingBlocks.Security;
using AICOS.IdentityOrganization.Api;
using AICOS.IdentityOrganization.Api.Domain;
using AICOS.IdentityOrganization.Api.Features.Authentication;
using AICOS.IdentityOrganization.Api.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AICOS.Services.SmokeTests;

public sealed class AuthenticationServiceTests
{
    private static readonly JwtOptions JwtOptions = new()
    {
        Issuer = "aicos-test",
        Audience = "aicos-test-clients",
        SigningKey = "identity-test-signing-key-0123456789-abcdef",
        ExpiryMinutes = 30,
    };

    [Fact]
    public void Identity_model_normalizes_email_and_uses_safe_defaults()
    {
        var user = new UserAccount(Guid.NewGuid(), "  STAFF@AICOS.VN  ", "Nhân viên AICOS");
        var branch = new Branch(Guid.NewGuid(), Guid.NewGuid(), "CN1", "Chi nhánh 1");

        user.Email.Should().Be("staff@aicos.vn");
        branch.Status.Should().Be(IdentityStatuses.Inactive);
    }

    [Fact]
    public void Identity_relationships_do_not_cascade_delete()
    {
        using var context = CreateContext();

        context.Model.GetEntityTypes()
            .SelectMany(entity => entity.GetForeignKeys())
            .Should()
            .NotBeEmpty()
            .And.OnlyContain(foreignKey => foreignKey.DeleteBehavior == DeleteBehavior.NoAction);
    }

    [Fact]
    public async Task Login_endpoint_returns_problem_details_for_invalid_request()
    {
        await using var factory = new WebApplicationFactory<IdentityOrganizationService>();
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/v1/identity/auth/login",
            new LoginRequest("not-an-email", string.Empty, null));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("errorCode").GetString().Should().Be("validation_failed");
    }

    [Fact]
    public async Task Login_issues_branch_scoped_token_for_active_staff()
    {
        await using var context = CreateContext();
        var data = await SeedAsync(context, branchCount: 1);
        var service = CreateService(context);

        var response = await service.LoginAsync(
            new LoginRequest(data.User.Email, "Correct-password-123", null),
            CancellationToken.None);

        response.RequiresBranchSelection.Should().BeFalse();
        response.Staff!.BranchId.Should().Be(data.BranchIds[0]);
        response.Staff.Role.Should().Be(AicosRoles.Waiter);
        var token = new JwtSecurityTokenHandler().ReadJwtToken(response.AccessToken);
        token.Claims.Single(claim => claim.Type == AicosClaimTypes.BranchId).Value
            .Should().Be(data.BranchIds[0].ToString());
    }

    [Fact]
    public async Task Login_requires_branch_selection_when_staff_has_multiple_assignments()
    {
        await using var context = CreateContext();
        var data = await SeedAsync(context, branchCount: 2);
        var service = CreateService(context);

        var response = await service.LoginAsync(
            new LoginRequest(data.User.Email, "Correct-password-123", null),
            CancellationToken.None);

        response.RequiresBranchSelection.Should().BeTrue();
        response.AccessToken.Should().BeNull();
        response.Branches.Should().HaveCount(2);
    }

    [Fact]
    public async Task Login_does_not_reveal_account_when_password_is_invalid()
    {
        await using var context = CreateContext();
        var data = await SeedAsync(context, branchCount: 1);
        var service = CreateService(context);

        var action = () => service.LoginAsync(
            new LoginRequest(data.User.Email, "wrong-password", null),
            CancellationToken.None);

        var exception = await action.Should().ThrowAsync<UnauthorizedException>();
        exception.Which.ErrorCode.Should().Be("auth.invalid_credentials");
    }

    private static IdentityOrganizationDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<IdentityOrganizationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new IdentityOrganizationDbContext(options);
    }

    private static AuthenticationService CreateService(IdentityOrganizationDbContext context) =>
        new(
            context,
            new PasswordHasher<UserAccount>(),
            new JwtTokenService(Options.Create(JwtOptions), TimeProvider.System),
            NullLogger<AuthenticationService>.Instance);

    private static async Task<TestIdentityData> SeedAsync(
        IdentityOrganizationDbContext context,
        int branchCount)
    {
        var tenantId = Guid.NewGuid();
        var user = new UserAccount(Guid.NewGuid(), "staff@aicos.vn", "Nhân viên AICOS");
        user.SetPasswordHash(
            new PasswordHasher<UserAccount>().HashPassword(user, "Correct-password-123"));
        var role = new Role(Guid.NewGuid(), "WAITER", "Nhân viên phục vụ");
        var membership = new TenantUser(Guid.NewGuid(), tenantId, user.Id, role.Id);
        var branchIds = new List<Guid>();

        context.Users.Add(user);
        context.Roles.Add(role);
        context.TenantUsers.Add(membership);
        for (var index = 1; index <= branchCount; index++)
        {
            var branch = new Branch(
                Guid.NewGuid(),
                tenantId,
                $"CN{index}",
                $"Chi nhánh {index}",
                IdentityStatuses.Active);
            branchIds.Add(branch.Id);
            context.Branches.Add(branch);
            context.StaffAssignments.Add(
                new StaffAssignment(Guid.NewGuid(), branch.Id, membership.Id));
        }

        await context.SaveChangesAsync();
        return new TestIdentityData(user, branchIds);
    }

    private sealed record TestIdentityData(UserAccount User, IReadOnlyList<Guid> BranchIds);
}
