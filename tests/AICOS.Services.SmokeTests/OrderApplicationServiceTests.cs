using AICOS.BuildingBlocks.Security;
using AICOS.Ordering.Api.Features.Orders;
using AICOS.Ordering.Api.Integrations;
using AICOS.Ordering.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace AICOS.Services.SmokeTests;

public sealed class OrderApplicationServiceTests
{
    [Fact]
    public async Task Takeaway_order_uses_catalog_prices_and_is_idempotent()
    {
        await using var context = CreateContext();
        var branchId = Guid.NewGuid(); var userId = Guid.NewGuid(); var variantId = Guid.NewGuid(); var optionId = Guid.NewGuid();
        var catalog = new CatalogContract(branchId, [new CategoryContract(Guid.NewGuid(), [new ProductContract(Guid.NewGuid(), "Cà phê sữa", [new VariantContract(variantId, "M", 39000m)], [new OptionGroupContract(Guid.NewGuid(), 0, 1, [new OptionContract(optionId, "Thêm shot", 10000m)])])])]);
        var service = new OrderApplicationService(context, new FakeTableClient(branchId), new FakeMenuClient(catalog), TimeProvider.System, NullLogger<OrderApplicationService>.Instance);
        var request = new CreateOrderRequest(Guid.NewGuid(), "TAKEAWAY", null, null, [new CreateOrderItemRequest(variantId, 2, "Ít đá", [optionId])]);
        var user = new TestCurrentUser(userId, branchId);

        var created = await service.CreateAsync(request, user, CancellationToken.None);
        var retried = await service.CreateAsync(request, user, CancellationToken.None);

        created.TotalAmount.Should().Be(98000m);
        created.Items.Single().CreatedByUserId.Should().Be(userId);
        retried.Id.Should().Be(created.Id);
        context.Orders.Should().ContainSingle();
    }

    private static OrderingDbContext CreateContext() => new(new DbContextOptionsBuilder<OrderingDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);
    private sealed class FakeMenuClient(CatalogContract catalog) : IMenuCatalogClient { public Task<CatalogContract> GetAsync(CancellationToken cancellationToken) => Task.FromResult(catalog); }
    private sealed class FakeTableClient(Guid branchId) : ITableSessionClient { public Task<TableSessionContract> GetAsync(Guid sessionId, CancellationToken cancellationToken) => Task.FromResult(new TableSessionContract(sessionId, branchId, "OPEN", [Guid.NewGuid()])); }
    private sealed class TestCurrentUser(Guid userId, Guid branchId) : ICurrentUser
    {
        public bool IsAuthenticated => true; public Guid? UserId => userId; public string? Name => "Order Staff";
        public string? Role => AicosRoles.Cashier; public Guid? BranchId => branchId; public bool IsInRole(string role) => role == AicosRoles.Cashier;
    }
}
