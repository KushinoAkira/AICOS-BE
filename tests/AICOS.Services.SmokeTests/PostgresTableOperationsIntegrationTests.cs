using System.Net.Http.Headers;
using System.Net.Http.Json;
using AICOS.BuildingBlocks.Security;
using AICOS.IdentityOrganization.Api.Domain;
using AICOS.IdentityOrganization.Api.Persistence;
using AICOS.TableReservation.Api;
using AICOS.TableReservation.Api.Domain;
using AICOS.TableReservation.Api.Features.TableOperations;
using AICOS.TableReservation.Api.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AICOS.Services.SmokeTests;

public sealed class PostgresTableOperationsIntegrationTests
{
    private const string ConnectionEnvironmentVariable = "AICOS_TEST_POSTGRES";

    [Fact]
    public async Task Table_lifecycle_uses_real_postgres_when_enabled()
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
        try
        {
            await SeedAsync(connectionString, ids);
            const string signingKey = "table-integration-signing-key-0123456789-abcdef";
            await using var factory = new WebApplicationFactory<TableReservationService>()
                .WithWebHostBuilder(builder => builder
                    .UseSetting("ConnectionStrings:Default", connectionString)
                    .UseSetting("Database:MigrateOnStartup", "false")
                    .UseSetting("Jwt:Issuer", "aicos-table-integration")
                    .UseSetting("Jwt:Audience", "aicos-table-integration-clients")
                    .UseSetting("Jwt:SigningKey", signingKey)
                    .UseSetting("Jwt:ExpiryMinutes", "30"));
            using var client = factory.CreateClient();
            var tokenService = new JwtTokenService(
                Options.Create(new JwtOptions
                {
                    Issuer = "aicos-table-integration",
                    Audience = "aicos-table-integration-clients",
                    SigningKey = signingKey,
                    ExpiryMinutes = 30,
                }),
                TimeProvider.System);
            var (token, _) = tokenService.CreateAccessToken(
                Guid.NewGuid(),
                "Table Integration Staff",
                AicosRoles.Waiter,
                ids.BranchId);
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", token);

            var map = await client.GetFromJsonAsync<TableMapResponse>("/api/v1/tables/map");
            map.Should().NotBeNull();
            map!.BranchId.Should().Be(ids.BranchId);
            map.Areas.Should().ContainSingle(area =>
                area.Id == ids.AreaId && area.Tables.Any(table => table.Id == ids.TableId));

            var openResponse = await client.PostAsJsonAsync(
                "/api/v1/tables/sessions",
                new OpenTableSessionRequest(ids.TableId));
            openResponse.EnsureSuccessStatusCode();
            var opened = await openResponse.Content.ReadFromJsonAsync<TableSessionResponse>();
            opened.Should().NotBeNull();
            opened!.Status.Should().Be(TableSessionStatuses.Open);

            var closeResponse = await client.PostAsync(
                $"/api/v1/tables/sessions/{opened.Id}/close",
                null);
            closeResponse.EnsureSuccessStatusCode();
            var closed = await closeResponse.Content.ReadFromJsonAsync<TableSessionResponse>();
            closed!.Status.Should().Be(TableSessionStatuses.Closed);

            var cleanedResponse = await client.PostAsync(
                $"/api/v1/tables/{ids.TableId}/cleaned",
                null);
            cleanedResponse.EnsureSuccessStatusCode();
            var cleaned = await cleanedResponse.Content.ReadFromJsonAsync<CafeTableResponse>();
            cleaned!.Status.Should().Be(CafeTableStatuses.Available);

            await using var verification = CreateTableContext(connectionString);
            (await verification.TableSessions.SingleAsync(session => session.Id == opened.Id))
                .Status.Should().Be(TableSessionStatuses.Closed);
            (await verification.CafeTables.SingleAsync(table => table.Id == ids.TableId))
                .Status.Should().Be(CafeTableStatuses.Available);
        }
        finally
        {
            await CleanupAsync(connectionString, ids);
        }
    }

    private static async Task SeedAsync(string connectionString, TestIds ids)
    {
        await using (var identity = CreateIdentityContext(connectionString))
        {
            identity.AddRange(
                new Tenant(ids.TenantId, "Table Integration Tenant", ids.TenantCode),
                new Branch(
                    ids.BranchId,
                    ids.TenantId,
                    ids.BranchCode,
                    "Table Integration Branch",
                    IdentityStatuses.Active));
            await identity.SaveChangesAsync();
        }

        await using var tables = CreateTableContext(connectionString);
        tables.AddRange(
            new TableArea(ids.AreaId, ids.BranchId, "Tầng trệt"),
            new CafeTable(ids.TableId, ids.AreaId, "S02", 4, ids.QrToken));
        await tables.SaveChangesAsync();
    }

    private static async Task CleanupAsync(string connectionString, TestIds ids)
    {
        await using (var tables = CreateTableContext(connectionString))
        {
            var sessionIds = await tables.TableSessions
                .Where(session => session.BranchId == ids.BranchId)
                .Select(session => session.Id)
                .ToListAsync();
            await tables.TableSessionTables
                .Where(link => link.TableId == ids.TableId || sessionIds.Contains(link.SessionId))
                .ExecuteDeleteAsync();
            await tables.TableSessions
                .Where(session => sessionIds.Contains(session.Id))
                .ExecuteDeleteAsync();
            await tables.CafeTables.Where(table => table.Id == ids.TableId).ExecuteDeleteAsync();
            await tables.TableAreas.Where(area => area.Id == ids.AreaId).ExecuteDeleteAsync();
        }

        await using var identity = CreateIdentityContext(connectionString);
        await identity.Branches.Where(branch => branch.Id == ids.BranchId).ExecuteDeleteAsync();
        await identity.Tenants.Where(tenant => tenant.Id == ids.TenantId).ExecuteDeleteAsync();
    }

    private static TableReservationDbContext CreateTableContext(string connectionString)
    {
        var options = new DbContextOptionsBuilder<TableReservationDbContext>()
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention()
            .Options;
        return new TableReservationDbContext(options);
    }

    private static IdentityOrganizationDbContext CreateIdentityContext(string connectionString)
    {
        var options = new DbContextOptionsBuilder<IdentityOrganizationDbContext>()
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention()
            .Options;
        return new IdentityOrganizationDbContext(options);
    }

    private sealed record TestIds(
        Guid TenantId,
        Guid BranchId,
        Guid AreaId,
        Guid TableId,
        string TenantCode,
        string BranchCode,
        string QrToken)
    {
        public static TestIds Create()
        {
            var suffix = Guid.NewGuid().ToString("N")[..10];
            return new TestIds(
                Guid.NewGuid(),
                Guid.NewGuid(),
                Guid.NewGuid(),
                Guid.NewGuid(),
                $"TB-{suffix}",
                $"TB-{suffix}",
                $"table-integration-{suffix}");
        }
    }
}
