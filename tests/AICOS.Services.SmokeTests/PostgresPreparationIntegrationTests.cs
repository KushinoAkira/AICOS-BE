using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AICOS.BuildingBlocks.Security;
using AICOS.IdentityOrganization.Api.Domain;
using AICOS.IdentityOrganization.Api.Persistence;
using AICOS.KitchenOperations.Api;
using AICOS.KitchenOperations.Api.Domain;
using AICOS.KitchenOperations.Api.Features.Preparation;
using AICOS.KitchenOperations.Api.Persistence;
using AICOS.MenuCatalog.Api.Domain;
using AICOS.MenuCatalog.Api.Persistence;
using AICOS.Ordering.Api.Domain;
using AICOS.Ordering.Api.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AICOS.Services.SmokeTests;

public sealed class PostgresPreparationIntegrationTests
{
    private const string ConnectionEnvironmentVariable = "AICOS_TEST_POSTGRES";

    [Fact]
    public async Task Preparation_queue_and_transitions_use_real_postgres_when_enabled()
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
            const string signingKey = "preparation-integration-key-0123456789-abcdef";
            await using var factory = new WebApplicationFactory<KitchenOperationsService>()
                .WithWebHostBuilder(builder => builder
                    .UseSetting("ConnectionStrings:Default", connectionString)
                    .UseSetting("Database:MigrateOnStartup", "false")
                    .UseSetting("Jwt:Issuer", "aicos-preparation-integration")
                    .UseSetting("Jwt:Audience", "aicos-preparation-clients")
                    .UseSetting("Jwt:SigningKey", signingKey)
                    .UseSetting("Jwt:ExpiryMinutes", "30"));
            using var client = factory.CreateClient();
            var tokenService = new JwtTokenService(
                Options.Create(new JwtOptions
                {
                    Issuer = "aicos-preparation-integration",
                    Audience = "aicos-preparation-clients",
                    SigningKey = signingKey,
                    ExpiryMinutes = 30,
                }),
                TimeProvider.System);
            var (token, _) = tokenService.CreateAccessToken(
                ids.UserId,
                "Preparation Integration Manager",
                AicosRoles.Manager,
                ids.BranchId);
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", token);

            var queue = await client.GetFromJsonAsync<PreparationQueueResponse>(
                "/api/v1/kitchen/queue");
            queue.Should().NotBeNull();
            queue!.Orders.Should().ContainSingle(order =>
                order.Id == ids.OrderId &&
                order.Items.Any(item => item.Id == ids.ItemId));

            var preparingRequests = Enumerable.Range(0, 2)
                .Select(_ => client.PatchAsJsonAsync(
                    $"/api/v1/kitchen/items/{ids.ItemId}/status",
                    new UpdatePreparationStatusRequest(PreparationStatuses.Preparing)))
                .ToArray();
            var preparingResponses = await Task.WhenAll(preparingRequests);
            preparingResponses.Should().ContainSingle(response => response.IsSuccessStatusCode);
            preparingResponses.Should().ContainSingle(response =>
                response.StatusCode == HttpStatusCode.Conflict);
            var conflictProblem = await preparingResponses
                .Single(response => response.StatusCode == HttpStatusCode.Conflict)
                .Content
                .ReadFromJsonAsync<JsonElement>();
            conflictProblem.GetProperty("errorCode").GetString()
                .Should().Be("preparation.concurrent_update");
            var preparing = await preparingResponses
                .Single(response => response.IsSuccessStatusCode)
                .Content
                .ReadFromJsonAsync<PreparationItemResponse>();
            preparing!.Status.Should().Be(PreparationStatuses.Preparing);

            var readyResponse = await client.PatchAsJsonAsync(
                $"/api/v1/kitchen/items/{ids.ItemId}/status",
                new UpdatePreparationStatusRequest(PreparationStatuses.Ready));
            readyResponse.EnsureSuccessStatusCode();
            var ready = await readyResponse.Content
                .ReadFromJsonAsync<PreparationItemResponse>();
            ready!.Status.Should().Be(PreparationStatuses.Ready);

            await using var verification = CreatePreparationContext(connectionString);
            (await verification.Items.SingleAsync(item => item.Id == ids.ItemId))
                .Status.Should().Be(PreparationStatuses.Ready);
            var transitionEvents = await verification.Events
                .Where(preparationEvent =>
                    preparationEvent.OrderItemId == ids.ItemId &&
                    preparationEvent.EventType == "STATUS_CHANGED")
                .ToListAsync();
            transitionEvents.Should().HaveCount(2);
            transitionEvents.Should().OnlyContain(preparationEvent =>
                preparationEvent.ActorUserId == ids.UserId);
        }
        finally
        {
            await CleanupAsync(connectionString, ids);
            await AssertNoTestDataAsync(connectionString, ids);
        }
    }

    private static async Task SeedAsync(string connectionString, TestIds ids)
    {
        await using (var identity = CreateIdentityContext(connectionString))
        {
            identity.AddRange(
                new Tenant(ids.TenantId, "Preparation Test Tenant", ids.Code),
                new Branch(
                    ids.BranchId,
                    ids.TenantId,
                    ids.Code,
                    "Preparation Test Branch",
                    IdentityStatuses.Active),
                new UserAccount(
                    ids.UserId,
                    $"preparation-{ids.Code}@aicos.test",
                    "Preparation Test Manager"));
            await identity.SaveChangesAsync();
        }

        await using (var catalog = CreateCatalogContext(connectionString))
        {
            catalog.AddRange(
                new Category(ids.CategoryId, ids.TenantId, "Cà phê"),
                new Product(ids.ProductId, ids.TenantId, ids.CategoryId, "Cà phê sữa"),
                new ProductVariant(ids.VariantId, ids.ProductId, "M", 39000m));
            await catalog.SaveChangesAsync();
        }

        await using var ordering = CreateOrderingContext(connectionString);
        var order = new Order(
            ids.OrderId,
            ids.BranchId,
            null,
            ids.Code,
            OrderTypes.Takeaway,
            null,
            DateTime.Now);
        order.SetTotals(39000m, 39000m);
        var item = new OrderItem(
            ids.ItemId,
            order.Id,
            ids.VariantId,
            "Cà phê sữa",
            "M",
            1,
            39000m,
            "Ít đá");
        var createdEvent = new OrderItemEvent(
            Guid.NewGuid(),
            item.Id,
            ids.UserId,
            DateTime.Now);
        ordering.AddRange(order, item, createdEvent);
        await ordering.SaveChangesAsync();
    }

    private static async Task CleanupAsync(string connectionString, TestIds ids)
    {
        await using (var ordering = CreateOrderingContext(connectionString))
        {
            await ordering.OrderItemEvents
                .Where(preparationEvent => preparationEvent.OrderItemId == ids.ItemId)
                .ExecuteDeleteAsync();
            await ordering.OrderItemOptions
                .Where(option => option.OrderItemId == ids.ItemId)
                .ExecuteDeleteAsync();
            await ordering.OrderItems
                .Where(item => item.Id == ids.ItemId)
                .ExecuteDeleteAsync();
            await ordering.Orders
                .Where(order => order.Id == ids.OrderId)
                .ExecuteDeleteAsync();
        }

        await using (var catalog = CreateCatalogContext(connectionString))
        {
            await catalog.ProductVariants
                .Where(variant => variant.Id == ids.VariantId)
                .ExecuteDeleteAsync();
            await catalog.Products
                .Where(product => product.Id == ids.ProductId)
                .ExecuteDeleteAsync();
            await catalog.Categories
                .Where(category => category.Id == ids.CategoryId)
                .ExecuteDeleteAsync();
        }

        await using var identity = CreateIdentityContext(connectionString);
        await identity.Branches
            .Where(branch => branch.Id == ids.BranchId)
            .ExecuteDeleteAsync();
        await identity.Users
            .Where(user => user.Id == ids.UserId)
            .ExecuteDeleteAsync();
        await identity.Tenants
            .Where(tenant => tenant.Id == ids.TenantId)
            .ExecuteDeleteAsync();
    }

    private static async Task AssertNoTestDataAsync(string connectionString, TestIds ids)
    {
        await using (var ordering = CreateOrderingContext(connectionString))
        {
            (await ordering.Orders.AnyAsync(order => order.Id == ids.OrderId))
                .Should().BeFalse();
            (await ordering.OrderItems.AnyAsync(item => item.Id == ids.ItemId))
                .Should().BeFalse();
            (await ordering.OrderItemEvents.AnyAsync(preparationEvent =>
                preparationEvent.OrderItemId == ids.ItemId))
                .Should().BeFalse();
        }

        await using (var catalog = CreateCatalogContext(connectionString))
        {
            (await catalog.Products.AnyAsync(product => product.Id == ids.ProductId))
                .Should().BeFalse();
            (await catalog.ProductVariants.AnyAsync(variant => variant.Id == ids.VariantId))
                .Should().BeFalse();
        }

        await using var identity = CreateIdentityContext(connectionString);
        (await identity.Tenants.AnyAsync(tenant => tenant.Id == ids.TenantId))
            .Should().BeFalse();
        (await identity.Branches.AnyAsync(branch => branch.Id == ids.BranchId))
            .Should().BeFalse();
        (await identity.Users.AnyAsync(user => user.Id == ids.UserId))
            .Should().BeFalse();
    }

    private static KitchenOperationsDbContext CreatePreparationContext(string connectionString)
    {
        var options = new DbContextOptionsBuilder<KitchenOperationsDbContext>()
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention()
            .Options;
        return new KitchenOperationsDbContext(options);
    }

    private static OrderingDbContext CreateOrderingContext(string connectionString)
    {
        var options = new DbContextOptionsBuilder<OrderingDbContext>()
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention()
            .Options;
        return new OrderingDbContext(options);
    }

    private static MenuCatalogDbContext CreateCatalogContext(string connectionString)
    {
        var options = new DbContextOptionsBuilder<MenuCatalogDbContext>()
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention()
            .Options;
        return new MenuCatalogDbContext(options);
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
        Guid UserId,
        Guid CategoryId,
        Guid ProductId,
        Guid VariantId,
        Guid OrderId,
        Guid ItemId,
        string Code)
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
                Guid.NewGuid(),
                Guid.NewGuid(),
                $"KP-{suffix}");
        }
    }
}
