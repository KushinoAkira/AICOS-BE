using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AICOS.BuildingBlocks.Exceptions;
using AICOS.BuildingBlocks.Security;
using AICOS.IdentityOrganization.Api;
using AICOS.IdentityOrganization.Api.Domain;
using AICOS.IdentityOrganization.Api.Features.Authentication;
using AICOS.IdentityOrganization.Api.Features.StaffProvisioning;
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

    private static readonly RefreshTokenOptions RefreshTokenOptions = new()
    {
        ExpiryDays = 30,
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
    public async Task Refresh_endpoint_validates_request_before_database_access()
    {
        await using var factory = new WebApplicationFactory<IdentityOrganizationService>();
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/v1/identity/auth/refresh",
            new RefreshRequest(string.Empty));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Me_endpoint_requires_an_access_token()
    {
        await using var factory = new WebApplicationFactory<IdentityOrganizationService>();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/v1/identity/auth/me");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Staff_provisioning_endpoint_requires_a_management_token()
    {
        await using var factory = new WebApplicationFactory<IdentityOrganizationService>();
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/v1/identity/staff",
            new CreateStaffRequest(
                "cashier@aicos.vn",
                "Strong-password-123!",
                "Thu ngân AICOS",
                Guid.NewGuid(),
                AicosRoles.Cashier));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
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
        response.RefreshToken.Should().NotBeNullOrWhiteSpace();
        response.RefreshTokenExpiresAtUtc.Should().BeAfter(DateTime.UtcNow.AddDays(29));
        var storedSession = context.RefreshSessions.Should().ContainSingle().Subject;
        storedSession.TokenHash.Should().NotBe(response.RefreshToken);
        storedSession.TokenHash.Should().Be(RefreshTokenFactory.Hash(response.RefreshToken!));
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

    [Fact]
    public async Task Refresh_rotates_token_and_rejects_the_previous_token()
    {
        await using var context = CreateContext();
        var data = await SeedAsync(context, branchCount: 1);
        var login = await CreateService(context).LoginAsync(
            new LoginRequest(data.User.Email, "Correct-password-123", null),
            CancellationToken.None);
        var sessions = CreateSessionService(context);

        var refreshed = await sessions.RefreshAsync(
            new RefreshRequest(login.RefreshToken!),
            CancellationToken.None);

        refreshed.RefreshToken.Should().NotBe(login.RefreshToken);
        context.RefreshSessions.Should().HaveCount(2);
        var action = () => sessions.RefreshAsync(
            new RefreshRequest(login.RefreshToken!),
            CancellationToken.None);
        var exception = await action.Should().ThrowAsync<UnauthorizedException>();
        exception.Which.ErrorCode.Should().Be("auth.invalid_refresh_token");
    }

    [Fact]
    public async Task Logout_is_idempotent_and_revokes_refresh_token()
    {
        await using var context = CreateContext();
        var data = await SeedAsync(context, branchCount: 1);
        var login = await CreateService(context).LoginAsync(
            new LoginRequest(data.User.Email, "Correct-password-123", null),
            CancellationToken.None);
        var sessions = CreateSessionService(context);
        var request = new LogoutRequest(login.RefreshToken!);

        await sessions.LogoutAsync(request, CancellationToken.None);
        await sessions.LogoutAsync(request, CancellationToken.None);

        var action = () => sessions.RefreshAsync(
            new RefreshRequest(login.RefreshToken!),
            CancellationToken.None);
        await action.Should().ThrowAsync<UnauthorizedException>();
    }

    [Fact]
    public async Task Me_returns_the_active_branch_scoped_profile()
    {
        await using var context = CreateContext();
        var data = await SeedAsync(context, branchCount: 1);

        var profile = await CreateSessionService(context).GetCurrentAsync(
            new TestCurrentUser(data.User.Id, data.BranchIds[0]),
            CancellationToken.None);

        profile.UserId.Should().Be(data.User.Id);
        profile.BranchId.Should().Be(data.BranchIds[0]);
        profile.Role.Should().Be(AicosRoles.Waiter);
    }

    [Fact]
    public async Task Manager_can_provision_staff_only_in_their_branch()
    {
        await using var context = CreateContext();
        var data = await SeedAsync(context, branchCount: 2);
        var service = CreateProvisioningService(context);

        var created = await service.CreateAsync(
            new CreateStaffRequest(
                "cashier@aicos.vn",
                "Strong-password-123!",
                "Thu ngân AICOS",
                data.BranchIds[0],
                "CASHIER"),
            new TestCurrentUser(data.User.Id, data.BranchIds[0], AicosRoles.Manager),
            CancellationToken.None);

        created.Role.Should().Be(AicosRoles.Cashier);
        created.BranchId.Should().Be(data.BranchIds[0]);
        context.Users.Should().Contain(user => user.Email == "cashier@aicos.vn");
        context.Roles.Should().Contain(role => role.Code == "CASHIER");
        context.TenantUsers.Should().HaveCount(2);
        context.StaffAssignments.Should().HaveCount(3);

        var crossBranch = () => service.CreateAsync(
            new CreateStaffRequest(
                "other@aicos.vn",
                "Strong-password-123!",
                "Nhân viên khác",
                data.BranchIds[1],
                AicosRoles.Waiter),
            new TestCurrentUser(data.User.Id, data.BranchIds[0], AicosRoles.Manager),
            CancellationToken.None);

        await crossBranch.Should().ThrowAsync<ForbiddenException>();
    }

    [Fact]
    public async Task Manager_cannot_provision_manager_and_duplicate_email_is_rejected()
    {
        await using var context = CreateContext();
        var data = await SeedAsync(context, branchCount: 1);
        var service = CreateProvisioningService(context);
        var manager = new TestCurrentUser(data.User.Id, data.BranchIds[0], AicosRoles.Manager);

        var elevatedRole = () => service.CreateAsync(
            new CreateStaffRequest(
                "manager@aicos.vn",
                "Strong-password-123!",
                "Quản lý khác",
                data.BranchIds[0],
                AicosRoles.Manager),
            manager,
            CancellationToken.None);
        await elevatedRole.Should().ThrowAsync<ForbiddenException>();

        var duplicateEmail = () => service.CreateAsync(
            new CreateStaffRequest(
                data.User.Email.ToUpperInvariant(),
                "Strong-password-123!",
                "Nhân viên trùng",
                data.BranchIds[0],
                AicosRoles.Cashier),
            manager,
            CancellationToken.None);
        var exception = await duplicateEmail.Should().ThrowAsync<ConflictException>();
        exception.Which.ErrorCode.Should().Be("identity.email_exists");
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
            CreateRefreshTokenFactory(),
            NullLogger<AuthenticationService>.Instance);

    private static SessionService CreateSessionService(IdentityOrganizationDbContext context) =>
        new(
            context,
            new JwtTokenService(Options.Create(JwtOptions), TimeProvider.System),
            CreateRefreshTokenFactory(),
            TimeProvider.System,
            NullLogger<SessionService>.Instance);

    private static StaffProvisioningService CreateProvisioningService(
        IdentityOrganizationDbContext context) =>
        new(
            context,
            new PasswordHasher<UserAccount>(),
            NullLogger<StaffProvisioningService>.Instance);

    private static RefreshTokenFactory CreateRefreshTokenFactory() =>
        new(Options.Create(RefreshTokenOptions), TimeProvider.System);

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

        context.Tenants.Add(new Tenant(tenantId, "AICOS Test Tenant", "TEST"));
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

    private sealed class TestCurrentUser(
        Guid userId,
        Guid? branchId,
        string role = AicosRoles.Waiter) : ICurrentUser
    {
        public bool IsAuthenticated => true;
        public Guid? UserId => userId;
        public string? Name => "Nhân viên AICOS";
        public string? Role => role;
        public Guid? BranchId => branchId;
        public bool IsInRole(string expectedRole) => role == expectedRole;
    }
}
