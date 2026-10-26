using System.Net.Http.Headers;
using System.Net.Http.Json;
using AICOS.IdentityOrganization.Api;
using AICOS.IdentityOrganization.Api.Domain;
using AICOS.IdentityOrganization.Api.Features.Authentication;
using AICOS.IdentityOrganization.Api.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

namespace AICOS.Services.SmokeTests;

public sealed class PostgresAuthenticationIntegrationTests
{
    private const string ConnectionEnvironmentVariable = "AICOS_TEST_POSTGRES";

    [PostgresIntegrationFact]
    public async Task Auth_round_trip_reads_and_writes_real_postgres_when_enabled()
    {
        var connectionString = Environment.GetEnvironmentVariable(ConnectionEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            if (string.Equals(
                    Environment.GetEnvironmentVariable("AICOS_REQUIRE_POSTGRES_INTEGRATION"),
                    "true",
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"{ConnectionEnvironmentVariable} is required for the PostgreSQL integration test.");
            }

            return;
        }

        var ids = TestIds.Create();
        var roleCreated = false;
        try
        {
            await using (var context = CreateContext(connectionString))
            {
                roleCreated = !await context.Roles.AnyAsync(role => role.Code == "WAITER");
                if (roleCreated)
                {
                    context.Roles.Add(new Role(ids.RoleId, "WAITER", "Nhân viên phục vụ"));
                }
                else
                {
                    ids = ids with
                    {
                        RoleId = await context.Roles
                            .Where(role => role.Code == "WAITER")
                            .Select(role => role.Id)
                            .SingleAsync(),
                    };
                }

                var user = new UserAccount(ids.UserId, ids.Email, "Integration Test Staff");
                user.SetPasswordHash(
                    new PasswordHasher<UserAccount>().HashPassword(user, ids.Password));
                context.AddRange(
                    new Tenant(ids.TenantId, "Integration Test Tenant", ids.TenantCode),
                    new Branch(
                        ids.BranchId,
                        ids.TenantId,
                        ids.BranchCode,
                        "Integration Test Branch",
                        IdentityStatuses.Active),
                    user,
                    new TenantUser(ids.MembershipId, ids.TenantId, ids.UserId, ids.RoleId),
                    new StaffAssignment(ids.AssignmentId, ids.BranchId, ids.MembershipId));
                await context.SaveChangesAsync();
            }

            await using var factory = new WebApplicationFactory<IdentityOrganizationService>()
                .WithWebHostBuilder(builder => builder
                    .UseSetting("ConnectionStrings:Default", connectionString)
                    .UseSetting("Database:MigrateOnStartup", "false"));
            using var client = factory.CreateClient();

            var loginResponse = await client.PostAsJsonAsync(
                "/api/v1/identity/auth/login",
                new LoginRequest(ids.Email, ids.Password, ids.BranchId));
            loginResponse.EnsureSuccessStatusCode();
            var login = await loginResponse.Content.ReadFromJsonAsync<LoginResponse>();
            login.Should().NotBeNull();
            login!.AccessToken.Should().NotBeNullOrWhiteSpace();
            login.RefreshToken.Should().NotBeNullOrWhiteSpace();

            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", login.AccessToken);
            var profile = await client.GetFromJsonAsync<StaffProfileResponse>(
                "/api/v1/identity/auth/me");
            profile!.UserId.Should().Be(ids.UserId);
            profile.BranchId.Should().Be(ids.BranchId);

            client.DefaultRequestHeaders.Authorization = null;
            var refreshResponse = await client.PostAsJsonAsync(
                "/api/v1/identity/auth/refresh",
                new RefreshRequest(login.RefreshToken!));
            refreshResponse.EnsureSuccessStatusCode();
            var refreshed = await refreshResponse.Content.ReadFromJsonAsync<RefreshResponse>();
            refreshed!.RefreshToken.Should().NotBe(login.RefreshToken);

            var logoutResponse = await client.PostAsJsonAsync(
                "/api/v1/identity/auth/logout",
                new LogoutRequest(refreshed.RefreshToken));
            logoutResponse.StatusCode.Should().Be(System.Net.HttpStatusCode.NoContent);
        }
        finally
        {
            await CleanupAsync(connectionString, ids, roleCreated);
        }
    }

    private static IdentityOrganizationDbContext CreateContext(string connectionString)
    {
        var options = new DbContextOptionsBuilder<IdentityOrganizationDbContext>()
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention()
            .Options;
        return new IdentityOrganizationDbContext(options);
    }

    private static async Task CleanupAsync(string connectionString, TestIds ids, bool roleCreated)
    {
        await using var context = CreateContext(connectionString);
        await context.RefreshSessions.Where(session => session.UserId == ids.UserId).ExecuteDeleteAsync();
        await context.StaffAssignments.Where(item => item.Id == ids.AssignmentId).ExecuteDeleteAsync();
        await context.TenantUsers.Where(item => item.Id == ids.MembershipId).ExecuteDeleteAsync();
        await context.Branches.Where(item => item.Id == ids.BranchId).ExecuteDeleteAsync();
        await context.Users.Where(item => item.Id == ids.UserId).ExecuteDeleteAsync();
        await context.Tenants.Where(item => item.Id == ids.TenantId).ExecuteDeleteAsync();
        if (roleCreated)
        {
            await context.Roles.Where(item => item.Id == ids.RoleId).ExecuteDeleteAsync();
        }
    }

    private sealed record TestIds(
        Guid TenantId,
        Guid BranchId,
        Guid UserId,
        Guid RoleId,
        Guid MembershipId,
        Guid AssignmentId,
        string TenantCode,
        string BranchCode,
        string Email,
        string Password)
    {
        public static TestIds Create()
        {
            var suffix = Guid.NewGuid().ToString("N")[..10];
            return new TestIds(
                Guid.NewGuid(),
                Guid.NewGuid(),
                Guid.NewGuid(),
                Guid.NewGuid(),
                Guid.NewGuid(),
                Guid.NewGuid(),
                $"IT-{suffix}",
                $"IT-{suffix}",
                $"integration-{suffix}@aicos.test",
                $"Integration-{suffix}-Password-123!");
        }
    }
}
