using System.Net;
using AICOS.BuildingBlocks.Exceptions;
using AICOS.BuildingBlocks.Security;
using AICOS.Payments.Api;
using AICOS.Payments.Api.Domain;
using AICOS.Payments.Api.Features.Billing;
using AICOS.Payments.Api.Integrations;
using AICOS.Payments.Api.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace AICOS.Services.SmokeTests;

public sealed class BillingServiceTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 10, 23, 15, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Payment_relationships_do_not_cascade_delete()
    {
        using var context = CreateContext();

        context.Model.GetEntityTypes()
            .SelectMany(entity => entity.GetForeignKeys())
            .Should()
            .NotBeEmpty()
            .And.OnlyContain(foreignKey => foreignKey.DeleteBehavior == DeleteBehavior.NoAction);
    }

    [Fact]
    public async Task Billing_endpoint_requires_an_access_token()
    {
        await using var factory = new WebApplicationFactory<PaymentsService>();
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/api/v1/payments/bills/{Guid.NewGuid()}");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Takeaway_bill_uses_order_snapshot_and_is_idempotent()
    {
        await using var context = CreateContext();
        var order = CreateTakeawayOrder();
        var service = CreateService(context, new FakeOrderClient(order));
        var request = new CreateBillRequest(Guid.NewGuid(), null, order.Id);
        var currentUser = new TestCurrentUser(Guid.NewGuid());

        var first = await service.CreateAsync(request, currentUser, CancellationToken.None);
        var retry = await service.CreateAsync(request, currentUser, CancellationToken.None);

        first.Id.Should().Be(request.RequestId);
        first.TotalAmount.Should().Be(98000m);
        first.Items.Should().ContainSingle().Which.Amount.Should().Be(98000m);
        retry.Id.Should().Be(first.Id);
        (await context.Bills.CountAsync()).Should().Be(1);
        (await context.BillItems.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Dine_in_order_must_be_billed_by_session()
    {
        await using var context = CreateContext();
        var takeaway = CreateTakeawayOrder();
        var dineIn = takeaway with { SessionId = Guid.NewGuid(), OrderType = "DINE_IN" };
        var service = CreateService(context, new FakeOrderClient(dineIn));

        var action = () => service.CreateAsync(
            new CreateBillRequest(Guid.NewGuid(), null, dineIn.Id),
            new TestCurrentUser(Guid.NewGuid()),
            CancellationToken.None);

        var exception = await action.Should().ThrowAsync<BusinessRuleException>();
        exception.Which.ErrorCode.Should().Be("billing.session_required");
    }

    private static PaymentsDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<PaymentsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new PaymentsDbContext(options);
    }

    private static BillingService CreateService(
        PaymentsDbContext context,
        IOrderBillingClient orders) =>
        new(
            context,
            orders,
            new FixedTimeProvider(Now),
            NullLogger<BillingService>.Instance);

    private static BillingOrderContract CreateTakeawayOrder() =>
        new(
            Guid.NewGuid(),
            "TAKEAWAY",
            null,
            98000m,
            [
                new BillingOrderItemContract(
                    Guid.NewGuid(),
                    2,
                    39000m,
                    [new BillingOrderOptionContract(10000m)])
            ]);

    private sealed class FakeOrderClient(BillingOrderContract order) : IOrderBillingClient
    {
        public Task<BillingOrderContract> GetAsync(
            Guid orderId,
            CancellationToken cancellationToken) =>
            Task.FromResult(order);

        public Task<IReadOnlyList<BillingOrderContract>> GetBySessionAsync(
            Guid sessionId,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<BillingOrderContract>>([order]);
    }

    private sealed class TestCurrentUser(Guid branchId) : ICurrentUser
    {
        public bool IsAuthenticated => true;
        public Guid? UserId { get; } = Guid.NewGuid();
        public string? Name => "Billing Test Cashier";
        public string? Role => AicosRoles.Cashier;
        public Guid? BranchId => branchId;
        public bool IsInRole(string role) => role == AicosRoles.Cashier;
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }
}
