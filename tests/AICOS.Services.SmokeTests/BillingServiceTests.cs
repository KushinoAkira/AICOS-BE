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
    public async Task Billing_source_endpoint_requires_an_access_token()
    {
        await using var factory = new WebApplicationFactory<PaymentsService>();
        using var client = factory.CreateClient();

        var response = await client.GetAsync(
            $"/api/v1/payments/bills?orderId={Guid.NewGuid()}");

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

    [Fact]
    public async Task Bill_can_be_loaded_by_order_source()
    {
        await using var context = CreateContext();
        var order = CreateTakeawayOrder();
        var service = CreateService(context, new FakeOrderClient(order));
        var currentUser = new TestCurrentUser(Guid.NewGuid());
        var created = await service.CreateAsync(
            new CreateBillRequest(Guid.NewGuid(), null, order.Id),
            currentUser,
            CancellationToken.None);

        var loaded = await service.GetBySourceAsync(
            null,
            order.Id,
            currentUser,
            CancellationToken.None);

        loaded.Should().BeEquivalentTo(created);
    }

    [Fact]
    public async Task Bill_can_be_loaded_by_session_source()
    {
        await using var context = CreateContext();
        var sessionId = Guid.NewGuid();
        var dineInOrder = CreateTakeawayOrder() with
        {
            OrderType = "DINE_IN",
            SessionId = sessionId,
        };
        var service = CreateService(context, new FakeOrderClient(dineInOrder));
        var currentUser = new TestCurrentUser(Guid.NewGuid());
        var created = await service.CreateAsync(
            new CreateBillRequest(Guid.NewGuid(), sessionId, null),
            currentUser,
            CancellationToken.None);

        var loaded = await service.GetBySourceAsync(
            sessionId,
            null,
            currentUser,
            CancellationToken.None);

        loaded.Should().BeEquivalentTo(created);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("00000000-0000-0000-0000-000000000000", null)]
    [InlineData(
        "11111111-1111-1111-1111-111111111111",
        "22222222-2222-2222-2222-222222222222")]
    public async Task Bill_source_query_requires_exactly_one_non_empty_source(
        string? sessionId,
        string? orderId)
    {
        await using var context = CreateContext();
        var order = CreateTakeawayOrder();
        var service = CreateService(context, new FakeOrderClient(order));

        var action = () => service.GetBySourceAsync(
            sessionId is null ? null : Guid.Parse(sessionId),
            orderId is null ? null : Guid.Parse(orderId),
            new TestCurrentUser(Guid.NewGuid()),
            CancellationToken.None);

        await action.Should().ThrowAsync<ValidationFailedException>();
    }

    [Fact]
    public async Task Session_bill_is_not_exposed_when_source_is_outside_branch()
    {
        await using var context = CreateContext();
        var sessionId = Guid.NewGuid();
        var bill = new Bill(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            sessionId,
            null,
            $"BL-{Guid.NewGuid():N}",
            98000m,
            Now.DateTime);
        context.Bills.Add(bill);
        await context.SaveChangesAsync();
        var service = CreateService(context, new EmptySessionOrderClient());

        var getById = () => service.GetAsync(
            bill.Id,
            new TestCurrentUser(Guid.NewGuid()),
            CancellationToken.None);
        var getBySource = () => service.GetBySourceAsync(
            sessionId,
            null,
            new TestCurrentUser(Guid.NewGuid()),
            CancellationToken.None);

        await getById.Should().ThrowAsync<NotFoundException>();
        await getBySource.Should().ThrowAsync<NotFoundException>();
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

    private sealed class EmptySessionOrderClient : IOrderBillingClient
    {
        public Task<BillingOrderContract> GetAsync(
            Guid orderId,
            CancellationToken cancellationToken) =>
            throw new NotFoundException("Order", orderId);

        public Task<IReadOnlyList<BillingOrderContract>> GetBySessionAsync(
            Guid sessionId,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<BillingOrderContract>>([]);
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
