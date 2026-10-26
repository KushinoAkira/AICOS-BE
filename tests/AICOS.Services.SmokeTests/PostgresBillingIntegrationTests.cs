using System.Net.Http.Headers;
using System.Net.Http.Json;
using AICOS.BuildingBlocks.Security;
using AICOS.IdentityOrganization.Api.Domain;
using AICOS.IdentityOrganization.Api.Persistence;
using AICOS.MenuCatalog.Api.Domain;
using AICOS.MenuCatalog.Api.Persistence;
using AICOS.Ordering.Api.Domain;
using AICOS.Ordering.Api.Persistence;
using AICOS.Payments.Api;
using AICOS.Payments.Api.Domain;
using AICOS.Payments.Api.Features.Billing;
using AICOS.Payments.Api.Integrations;
using AICOS.Payments.Api.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace AICOS.Services.SmokeTests;

public sealed class PostgresBillingIntegrationTests
{
    [PostgresIntegrationFact]
    public async Task Create_retry_and_read_bill_use_real_postgres_when_enabled()
    {
        var connectionString = Environment.GetEnvironmentVariable("AICOS_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            if (string.Equals(
                    Environment.GetEnvironmentVariable("AICOS_REQUIRE_POSTGRES_INTEGRATION"),
                    "true",
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "AICOS_TEST_POSTGRES is required for the PostgreSQL integration test.");
            }

            return;
        }

        PaymentPostgresTestDatabaseGuard.RequireDedicatedDatabase(connectionString);

        var ids = TestIds.Create();
        try
        {
            var contract = await SeedAsync(connectionString, ids);
            const string signingKey = "billing-integration-key-0123456789-abcdef";
            await using var factory = new WebApplicationFactory<PaymentsService>()
                .WithWebHostBuilder(builder =>
                {
                    builder.UseSetting("ConnectionStrings:Default", connectionString)
                        .UseSetting("Database:MigrateOnStartup", "false")
                        .UseSetting("Jwt:Issuer", "aicos-billing-integration")
                        .UseSetting("Jwt:Audience", "aicos-billing-clients")
                        .UseSetting("Jwt:SigningKey", signingKey);
                    builder.ConfigureServices(services =>
                    {
                        services.RemoveAll<IOrderBillingClient>();
                        services.AddSingleton<IOrderBillingClient>(
                            new FakeOrderClient(contract));
                    });
                });
            using var client = factory.CreateClient();
            var tokenService = new JwtTokenService(
                Options.Create(new JwtOptions
                {
                    Issuer = "aicos-billing-integration",
                    Audience = "aicos-billing-clients",
                    SigningKey = signingKey,
                    ExpiryMinutes = 30,
                }),
                TimeProvider.System);
            var (token, _) = tokenService.CreateAccessToken(
                ids.UserId,
                "Billing Integration Cashier",
                AicosRoles.Cashier,
                ids.BranchId);
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", token);
            var request = new CreateBillRequest(ids.BillId, null, ids.OrderId);

            var create = await client.PostAsJsonAsync("/api/v1/payments/bills", request);
            create.EnsureSuccessStatusCode();
            var first = (await create.Content.ReadFromJsonAsync<BillResponse>())!;
            var retry = await client.PostAsJsonAsync("/api/v1/payments/bills", request);
            retry.EnsureSuccessStatusCode();
            var loaded = await client.GetFromJsonAsync<BillResponse>(
                $"/api/v1/payments/bills/{ids.BillId}");
            var loadedBySource = await client.GetFromJsonAsync<BillResponse>(
                $"/api/v1/payments/bills?orderId={ids.OrderId}");

            first.TotalAmount.Should().Be(98000m);
            loaded!.Id.Should().Be(first.Id);
            loadedBySource!.Id.Should().Be(first.Id);
            await using var verification = CreatePaymentContext(connectionString);
            (await verification.Bills.CountAsync(bill => bill.Id == ids.BillId))
                .Should().Be(1);
            (await verification.BillItems.CountAsync(item => item.BillId == ids.BillId))
                .Should().Be(1);
        }
        finally
        {
            await CleanupAsync(connectionString, ids);
            await AssertNoTestDataAsync(connectionString, ids);
        }
    }

    private static async Task<BillingOrderContract> SeedAsync(
        string connectionString,
        TestIds ids)
    {
        await using (var identity = CreateIdentityContext(connectionString))
        {
            identity.AddRange(
                new Tenant(ids.TenantId, "Billing Test Tenant", ids.Code),
                new Branch(
                    ids.BranchId,
                    ids.TenantId,
                    ids.Code,
                    "Billing Test Branch",
                    IdentityStatuses.Active),
                new UserAccount(
                    ids.UserId,
                    $"billing-{ids.Code}@aicos.test",
                    "Billing Test Cashier"));
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
        order.SetTotals(98000m, 98000m);
        var item = new OrderItem(
            ids.ItemId,
            order.Id,
            ids.VariantId,
            "Cà phê sữa",
            "M",
            2,
            49000m,
            null);
        ordering.AddRange(
            order,
            item,
            new OrderItemEvent(Guid.NewGuid(), item.Id, ids.UserId, DateTime.Now));
        await ordering.SaveChangesAsync();

        return new BillingOrderContract(
            order.Id,
            OrderTypes.Takeaway,
            null,
            98000m,
            [new BillingOrderItemContract(item.Id, 2, 49000m, [])]);
    }

    private static async Task CleanupAsync(string connectionString, TestIds ids)
    {
        await using (var payments = CreatePaymentContext(connectionString))
        {
            var paymentIds = await payments.Payments
                .Where(payment => payment.BillId == ids.BillId)
                .Select(payment => payment.Id)
                .ToListAsync();
            await payments.Refunds
                .Where(refund => paymentIds.Contains(refund.PaymentId))
                .ExecuteDeleteAsync();
            await payments.Payments
                .Where(payment => paymentIds.Contains(payment.Id))
                .ExecuteDeleteAsync();
            await payments.BillItems
                .Where(item => item.BillId == ids.BillId)
                .ExecuteDeleteAsync();
            await payments.Bills
                .Where(bill => bill.Id == ids.BillId)
                .ExecuteDeleteAsync();
        }

        await using (var ordering = CreateOrderingContext(connectionString))
        {
            await ordering.OrderItemEvents
                .Where(orderEvent => orderEvent.OrderItemId == ids.ItemId)
                .ExecuteDeleteAsync();
            await ordering.OrderItemOptions
                .Where(option => option.OrderItemId == ids.ItemId)
                .ExecuteDeleteAsync();
            await ordering.OrderItems.Where(item => item.Id == ids.ItemId).ExecuteDeleteAsync();
            await ordering.Orders.Where(order => order.Id == ids.OrderId).ExecuteDeleteAsync();
        }

        await using (var catalog = CreateCatalogContext(connectionString))
        {
            await catalog.ProductVariants.Where(x => x.Id == ids.VariantId).ExecuteDeleteAsync();
            await catalog.Products.Where(x => x.Id == ids.ProductId).ExecuteDeleteAsync();
            await catalog.Categories.Where(x => x.Id == ids.CategoryId).ExecuteDeleteAsync();
        }

        await using var identity = CreateIdentityContext(connectionString);
        await identity.Branches.Where(x => x.Id == ids.BranchId).ExecuteDeleteAsync();
        await identity.Users.Where(x => x.Id == ids.UserId).ExecuteDeleteAsync();
        await identity.Tenants.Where(x => x.Id == ids.TenantId).ExecuteDeleteAsync();
    }

    private static async Task AssertNoTestDataAsync(string connectionString, TestIds ids)
    {
        await using var payments = CreatePaymentContext(connectionString);
        (await payments.Bills.AnyAsync(bill => bill.Id == ids.BillId)).Should().BeFalse();
        (await payments.BillItems.AnyAsync(item => item.BillId == ids.BillId)).Should().BeFalse();
    }

    private static PaymentsDbContext CreatePaymentContext(string cs) =>
        new(new DbContextOptionsBuilder<PaymentsDbContext>()
            .UseNpgsql(cs).UseSnakeCaseNamingConvention().Options);

    private static OrderingDbContext CreateOrderingContext(string cs) =>
        new(new DbContextOptionsBuilder<OrderingDbContext>()
            .UseNpgsql(cs).UseSnakeCaseNamingConvention().Options);

    private static MenuCatalogDbContext CreateCatalogContext(string cs) =>
        new(new DbContextOptionsBuilder<MenuCatalogDbContext>()
            .UseNpgsql(cs).UseSnakeCaseNamingConvention().Options);

    private static IdentityOrganizationDbContext CreateIdentityContext(string cs) =>
        new(new DbContextOptionsBuilder<IdentityOrganizationDbContext>()
            .UseNpgsql(cs).UseSnakeCaseNamingConvention().Options);

    private sealed class FakeOrderClient(BillingOrderContract order) : IOrderBillingClient
    {
        public Task<BillingOrderContract> GetAsync(Guid id, CancellationToken ct) =>
            Task.FromResult(order);

        public Task<IReadOnlyList<BillingOrderContract>> GetBySessionAsync(
            Guid id,
            CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<BillingOrderContract>>([order]);
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
        Guid BillId,
        string Code)
    {
        public static TestIds Create()
        {
            var suffix = Guid.NewGuid().ToString("N")[..10];
            return new TestIds(
                Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
                Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
                Guid.NewGuid(), $"BL-{suffix}");
        }
    }
}
