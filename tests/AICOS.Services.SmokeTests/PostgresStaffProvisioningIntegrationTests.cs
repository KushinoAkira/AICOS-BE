using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AICOS.IdentityOrganization.Api;
using AICOS.IdentityOrganization.Api.Domain;
using AICOS.IdentityOrganization.Api.Features.Authentication;
using AICOS.IdentityOrganization.Api.Features.StaffProvisioning;
using AICOS.IdentityOrganization.Api.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

namespace AICOS.Services.SmokeTests;

public sealed class PostgresStaffProvisioningIntegrationTests
{
    private const string ConnectionEnvironmentVariable = "AICOS_TEST_POSTGRES";

    [Fact]
    public async Task Manager_provisions_cashier_in_real_postgres_when_enabled()
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
        var managerRoleCreated = false;
        var cashierRoleExisted = false;
        Guid? createdCashierId = null;

        try
        {
            await using (var context = CreateContext(connectionString))
            {
                var managerRole = await context.Roles.SingleOrDefaultAsync(
                    role => role.Code == "MANAGER");
                managerRoleCreated = managerRole is null;
                if (managerRole is null)
                {
                    managerRole = new Role(ids.ManagerRoleId, "MANAGER", "Quản lý");
                    context.Roles.Add(managerRole);
                }
                else
                {
                    ids = ids with { ManagerRoleId = managerRole.Id };
                }

                cashierRoleExisted = await context.Roles.AnyAsync(role => role.Code == "CASHIER");
                var manager = new UserAccount(ids.ManagerId, ids.ManagerEmail, "Integration Test Manager");
                manager.SetPasswordHash(
                    new PasswordHasher<UserAccount>().HashPassword(manager, ids.ManagerPassword));

                context.AddRange(
                    new Tenant(ids.TenantId, "Provisioning Test Tenant", ids.TenantCode),
                    new Branch(
                        ids.BranchId,
                        ids.TenantId,
                        ids.BranchCode,
                        "Provisioning Test Branch",
                        IdentityStatuses.Active),
                    manager,
                    new TenantUser(
                        ids.ManagerMembershipId,
                        ids.TenantId,
                        ids.ManagerId,
                        ids.ManagerRoleId),
                    new StaffAssignment(
                        ids.ManagerAssignmentId,
                        ids.BranchId,
                        ids.ManagerMembershipId));
                await context.SaveChangesAsync();
            }

            await using var factory = new WebApplicationFactory<IdentityOrganizationService>()
                .WithWebHostBuilder(builder => builder
                    .UseSetting("ConnectionStrings:Default", connectionString)
                    .UseSetting("Database:MigrateOnStartup", "false"));
            using var client = factory.CreateClient();

            var managerLogin = await LoginAsync(
                client,
                ids.ManagerEmail,
                ids.ManagerPassword,
                ids.BranchId);
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", managerLogin.AccessToken);

            var createResponse = await client.PostAsJsonAsync(
                "/api/v1/identity/staff",
                new CreateStaffRequest(
                    ids.CashierEmail,
                    ids.CashierPassword,
                    "Integration Test Cashier",
                    ids.BranchId,
                    "CASHIER"));
            createResponse.StatusCode.Should().Be(HttpStatusCode.Created);
            var created = await createResponse.Content.ReadFromJsonAsync<StaffAccountResponse>();
            created.Should().NotBeNull();
            createdCashierId = created!.UserId;
            created.Role.Should().Be("Cashier");
            created.TenantId.Should().Be(ids.TenantId);

            var duplicateResponse = await client.PostAsJsonAsync(
                "/api/v1/identity/staff",
                new CreateStaffRequest(
                    ids.CashierEmail,
                    ids.CashierPassword,
                    "Duplicate Cashier",
                    ids.BranchId,
                    "CASHIER"));
            duplicateResponse.StatusCode.Should().Be(HttpStatusCode.Conflict);

            client.DefaultRequestHeaders.Authorization = null;
            var cashierLogin = await LoginAsync(
                client,
                ids.CashierEmail,
                ids.CashierPassword,
                ids.BranchId);
            cashierLogin.Staff!.Role.Should().Be("Cashier");
        }
        finally
        {
            await CleanupAsync(
                connectionString,
                ids,
                createdCashierId,
                managerRoleCreated,
                cashierRoleExisted);
        }
    }

    private static async Task<LoginResponse> LoginAsync(
        HttpClient client,
        string email,
        string password,
        Guid branchId)
    {
        var response = await client.PostAsJsonAsync(
            "/api/v1/identity/auth/login",
            new LoginRequest(email, password, branchId));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<LoginResponse>())!;
    }

    private static IdentityOrganizationDbContext CreateContext(string connectionString)
    {
        var options = new DbContextOptionsBuilder<IdentityOrganizationDbContext>()
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention()
            .Options;
        return new IdentityOrganizationDbContext(options);
    }

    private static async Task CleanupAsync(
        string connectionString,
        TestIds ids,
        Guid? cashierId,
        bool managerRoleCreated,
        bool cashierRoleExisted)
    {
        await using var context = CreateContext(connectionString);
        var userIds = new[] { ids.ManagerId, cashierId ?? Guid.Empty };
        await context.RefreshSessions.Where(session => userIds.Contains(session.UserId)).ExecuteDeleteAsync();

        if (cashierId is not null)
        {
            var cashierMembershipIds = await context.TenantUsers
                .Where(item => item.UserId == cashierId)
                .Select(item => item.Id)
                .ToListAsync();
            await context.StaffAssignments
                .Where(item => cashierMembershipIds.Contains(item.TenantUserId))
                .ExecuteDeleteAsync();
            await context.TenantUsers.Where(item => item.UserId == cashierId).ExecuteDeleteAsync();
            await context.Users.Where(item => item.Id == cashierId).ExecuteDeleteAsync();
        }

        await context.StaffAssignments
            .Where(item => item.Id == ids.ManagerAssignmentId)
            .ExecuteDeleteAsync();
        await context.TenantUsers
            .Where(item => item.Id == ids.ManagerMembershipId)
            .ExecuteDeleteAsync();
        await context.Branches.Where(item => item.Id == ids.BranchId).ExecuteDeleteAsync();
        await context.Users.Where(item => item.Id == ids.ManagerId).ExecuteDeleteAsync();
        await context.Tenants.Where(item => item.Id == ids.TenantId).ExecuteDeleteAsync();

        if (managerRoleCreated)
        {
            await context.Roles.Where(item => item.Id == ids.ManagerRoleId).ExecuteDeleteAsync();
        }

        if (!cashierRoleExisted)
        {
            await context.Roles
                .Where(item => item.Code == "CASHIER" &&
                    !context.TenantUsers.Any(membership => membership.RoleId == item.Id))
                .ExecuteDeleteAsync();
        }
    }

    private sealed record TestIds(
        Guid TenantId,
        Guid BranchId,
        Guid ManagerId,
        Guid ManagerRoleId,
        Guid ManagerMembershipId,
        Guid ManagerAssignmentId,
        string TenantCode,
        string BranchCode,
        string ManagerEmail,
        string ManagerPassword,
        string CashierEmail,
        string CashierPassword)
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
                $"PV-{suffix}",
                $"PV-{suffix}",
                $"manager-{suffix}@aicos.test",
                $"Manager-{suffix}-Password-123!",
                $"cashier-{suffix}@aicos.test",
                $"Cashier-{suffix}-Password-123!");
        }
    }
}
