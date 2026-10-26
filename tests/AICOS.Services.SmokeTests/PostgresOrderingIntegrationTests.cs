using System.Net.Http.Headers;
using System.Net.Http.Json;
using AICOS.BuildingBlocks.Security;
using AICOS.IdentityOrganization.Api.Domain;
using AICOS.IdentityOrganization.Api.Persistence;
using AICOS.MenuCatalog.Api.Domain;
using AICOS.MenuCatalog.Api.Persistence;
using AICOS.Ordering.Api;
using AICOS.Ordering.Api.Features.Orders;
using AICOS.Ordering.Api.Integrations;
using AICOS.Ordering.Api.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace AICOS.Services.SmokeTests;

public sealed class PostgresOrderingIntegrationTests
{
    [PostgresIntegrationFact]
    public async Task Takeaway_create_retry_and_tracking_use_real_postgres_when_enabled()
    {
        var connectionString = Environment.GetEnvironmentVariable("AICOS_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            if (string.Equals(Environment.GetEnvironmentVariable("AICOS_REQUIRE_POSTGRES_INTEGRATION"), "true", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("AICOS_TEST_POSTGRES is required for the PostgreSQL integration test.");
            return;
        }

        var ids = TestIds.Create();
        try
        {
            await SeedAsync(connectionString, ids);
            const string key = "ordering-integration-signing-key-0123456789-abcdef";
            var catalog = new CatalogContract(ids.BranchId,
            [
                new CategoryContract(ids.CategoryId,
                [
                    new ProductContract(ids.ProductId, "Cà phê sữa",
                        [new VariantContract(ids.VariantId, "M", 39000m)],
                        [new OptionGroupContract(ids.GroupId, 0, 1,
                            [new OptionContract(ids.OptionId, "Thêm shot", 10000m)])])
                ])
            ]);
            await using var factory = new WebApplicationFactory<OrderingService>()
                .WithWebHostBuilder(builder =>
                {
                    builder.UseSetting("ConnectionStrings:Default", connectionString)
                        .UseSetting("Database:MigrateOnStartup", "false")
                        .UseSetting("Jwt:Issuer", "aicos-ordering-integration")
                        .UseSetting("Jwt:Audience", "aicos-ordering-clients")
                        .UseSetting("Jwt:SigningKey", key);
                    builder.ConfigureServices(services =>
                    {
                        services.RemoveAll<IMenuCatalogClient>();
                        services.RemoveAll<ITableSessionClient>();
                        services.AddSingleton<IMenuCatalogClient>(new FakeMenuClient(catalog));
                        services.AddSingleton<ITableSessionClient>(new FakeTableClient(ids.BranchId));
                    });
                });
            using var client = factory.CreateClient();
            var jwt = new JwtTokenService(Options.Create(new JwtOptions
            {
                Issuer = "aicos-ordering-integration",
                Audience = "aicos-ordering-clients",
                SigningKey = key,
                ExpiryMinutes = 30,
            }), TimeProvider.System);
            var (token, _) = jwt.CreateAccessToken(ids.UserId, "Ordering Staff", AicosRoles.Cashier, ids.BranchId);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var request = new CreateOrderRequest(ids.OrderId, "TAKEAWAY", null, null,
                [new CreateOrderItemRequest(ids.VariantId, 2, "Ít đá", [ids.OptionId])]);

            var create = await client.PostAsJsonAsync("/api/v1/orders/", request);
            create.EnsureSuccessStatusCode();
            var first = (await create.Content.ReadFromJsonAsync<OrderResponse>())!;
            var retry = await client.PostAsJsonAsync("/api/v1/orders/", request);
            retry.EnsureSuccessStatusCode();
            var tracked = await client.GetFromJsonAsync<OrderResponse>($"/api/v1/orders/{ids.OrderId}");

            first.TotalAmount.Should().Be(98000m);
            tracked!.Id.Should().Be(first.Id);
            tracked.Items.Single().CreatedByUserId.Should().Be(ids.UserId);
            await using var verification = CreateOrderContext(connectionString);
            (await verification.Orders.CountAsync(x => x.Id == ids.OrderId)).Should().Be(1);
        }
        finally
        {
            await CleanupAsync(connectionString, ids);
        }
    }

    private static async Task SeedAsync(string connectionString, TestIds x)
    {
        await using (var identity = CreateIdentityContext(connectionString))
        {
            identity.AddRange(new Tenant(x.TenantId, "Order Test Tenant", x.Code),
                new Branch(x.BranchId, x.TenantId, x.Code, "Order Test Branch", IdentityStatuses.Active),
                new UserAccount(x.UserId, $"order-{x.Code}@aicos.test", "Order Test Staff"));
            await identity.SaveChangesAsync();
        }
        await using var catalog = CreateCatalogContext(connectionString);
        catalog.AddRange(new Category(x.CategoryId, x.TenantId, "Cà phê"),
            new Product(x.ProductId, x.TenantId, x.CategoryId, "Cà phê sữa"),
            new ProductVariant(x.VariantId, x.ProductId, "M", 39000m),
            new ProductOptionGroup(x.GroupId, x.ProductId, "Topping"),
            new ProductOption(x.OptionId, x.GroupId, "Thêm shot", 10000m),
            new BranchProduct(x.BranchProductId, x.BranchId, x.ProductId));
        await catalog.SaveChangesAsync();
    }

    private static async Task CleanupAsync(string cs, TestIds x)
    {
        await using (var orders = CreateOrderContext(cs))
        {
            var itemIds = await orders.OrderItems.Where(i => i.OrderId == x.OrderId).Select(i => i.Id).ToListAsync();
            await orders.OrderItemEvents.Where(e => itemIds.Contains(e.OrderItemId)).ExecuteDeleteAsync();
            await orders.OrderItemOptions.Where(o => itemIds.Contains(o.OrderItemId)).ExecuteDeleteAsync();
            await orders.OrderItems.Where(i => itemIds.Contains(i.Id)).ExecuteDeleteAsync();
            await orders.Orders.Where(o => o.Id == x.OrderId).ExecuteDeleteAsync();
        }
        await using (var catalog = CreateCatalogContext(cs))
        {
            await catalog.ProductOptions.Where(i => i.Id == x.OptionId).ExecuteDeleteAsync();
            await catalog.ProductOptionGroups.Where(i => i.Id == x.GroupId).ExecuteDeleteAsync();
            await catalog.ProductVariants.Where(i => i.Id == x.VariantId).ExecuteDeleteAsync();
            await catalog.BranchProducts.Where(i => i.Id == x.BranchProductId).ExecuteDeleteAsync();
            await catalog.Products.Where(i => i.Id == x.ProductId).ExecuteDeleteAsync();
            await catalog.Categories.Where(i => i.Id == x.CategoryId).ExecuteDeleteAsync();
        }
        await using var identity = CreateIdentityContext(cs);
        await identity.Branches.Where(i => i.Id == x.BranchId).ExecuteDeleteAsync();
        await identity.Users.Where(i => i.Id == x.UserId).ExecuteDeleteAsync();
        await identity.Tenants.Where(i => i.Id == x.TenantId).ExecuteDeleteAsync();
    }

    private static OrderingDbContext CreateOrderContext(string cs) => new(new DbContextOptionsBuilder<OrderingDbContext>().UseNpgsql(cs).UseSnakeCaseNamingConvention().Options);
    private static MenuCatalogDbContext CreateCatalogContext(string cs) => new(new DbContextOptionsBuilder<MenuCatalogDbContext>().UseNpgsql(cs).UseSnakeCaseNamingConvention().Options);
    private static IdentityOrganizationDbContext CreateIdentityContext(string cs) => new(new DbContextOptionsBuilder<IdentityOrganizationDbContext>().UseNpgsql(cs).UseSnakeCaseNamingConvention().Options);
    private sealed class FakeMenuClient(CatalogContract catalog) : IMenuCatalogClient { public Task<CatalogContract> GetAsync(CancellationToken ct) => Task.FromResult(catalog); }
    private sealed class FakeTableClient(Guid branchId) : ITableSessionClient { public Task<TableSessionContract> GetAsync(Guid id, CancellationToken ct) => Task.FromResult(new TableSessionContract(id, branchId, "OPEN", [Guid.NewGuid()])); }
    private sealed record TestIds(Guid TenantId, Guid BranchId, Guid UserId, Guid CategoryId, Guid ProductId, Guid VariantId, Guid GroupId, Guid OptionId, Guid BranchProductId, Guid OrderId, string Code)
    {
        public static TestIds Create() => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), $"OR-{Guid.NewGuid():N}"[..13]);
    }
}
