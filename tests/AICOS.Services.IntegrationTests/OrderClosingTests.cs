using System.Net.Http.Json;
using System.Text.Json;
using AICOS.BuildingBlocks.Exceptions;
using AICOS.BuildingBlocks.Security;
using AICOS.Contracts.Events;
using AICOS.Ordering.Api;
using AICOS.Ordering.Api.Persistence;
using AICOS.Ordering.Api.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AICOS.Services.IntegrationTests;

/// <summary>An order that ends without being handed over: a no-show (nobody came), unfulfillable (the shop could not make it), and the remake of a ready order.</summary>
public sealed class OrderClosingTests : IDisposable
{
    private const string Orders = "/api/v1/orders";
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 3, 0, 0, TimeSpan.Zero);          // Monday 10:00 local

    private readonly FakeMenu _menu = new();
    private readonly FakeVouchers _vouchers = new();
    private readonly FakePayments _payments = new();
    private readonly ServiceFactory<OrderingService, OrderingDbContext> _f;
    private readonly Guid _tenant = ServiceFactory<OrderingService, OrderingDbContext>.DefaultTenantId;
    private readonly Guid _b1;
    private readonly Guid _b2;
    private readonly Guid _latte;
    private readonly Guid _customerId = Guid.NewGuid();
    private readonly HttpClient _customer;
    private readonly HttpClient _cashier;
    private readonly HttpClient _barista;
    private readonly HttpClient _manager;

    public OrderClosingTests()
    {
        _f = new ServiceFactory<OrderingService, OrderingDbContext>(services =>
        {
            services.RemoveAll<IMenuCatalogClient>();
            services.AddSingleton<IMenuCatalogClient>(_menu);
            services.RemoveAll<IVoucherClient>();
            services.AddSingleton<IVoucherClient>(_vouchers);
            services.RemoveAll<IPaymentClient>();
            services.AddSingleton<IPaymentClient>(_payments);
        });
        _f.Clock.Now = Now;
        _b1 = _f.Branches.Add(_tenant, name: "Quận 1");
        _b2 = _f.Branches.Add(_tenant, name: "Quận 3");
        _latte = _menu.Add(_b1, "Latte", 55_000);
        _customer = _f.As(AicosRoles.Customer, userId: _customerId);
        _cashier = _f.As(AicosRoles.Cashier, _b1);
        _barista = _f.As(AicosRoles.Barista, _b1);
        _manager = _f.As(AicosRoles.BranchManager, _b1);
    }

    public void Dispose() => _f.Dispose();

    private static DateTime Slot(int localHour = 11, int minute = 0) => new DateTime(2026, 10, 26, 0, 0, 0, DateTimeKind.Utc).AddHours(localHour - 7).AddMinutes(minute);

    private async Task<JsonElement> PickupAsync(string? promo = null) =>
        await _customer.PostOk($"{Orders}/pickup", new { branchId = _b1, slotStartUtc = Slot(), promotionCode = promo, items = new object[] { new { productId = _latte, quantity = 2 } } });

    private async Task<JsonElement> CounterOrderAsync() =>
        await _cashier.PostOk(Orders, new { type = "Takeaway", items = new object[] { new { productId = _latte, quantity = 1 } } });

    private PaymentSucceededEvent Paid(JsonElement order, Guid? paymentId = null) =>
        new(_tenant, _b1, paymentId ?? Guid.NewGuid(), order.Id(), order.GetProperty("finalAmount").GetDecimal(), "Mock", Now.UtcDateTime);

    private async Task<JsonElement> GetAsync(Guid id) => await _manager.GetFromJsonAsync<JsonElement>($"{Orders}/{id}");

    private Task BarStartsAsync(JsonElement order) => _f.HandleAsync(new KitchenOrderStartedIntegrationEvent(_tenant, Guid.NewGuid(), order.Id(), _b1, DateTime.UtcNow));

    private Task BarFinishesAsync(JsonElement order) => _f.HandleAsync(new KitchenOrderReadyIntegrationEvent(_tenant, Guid.NewGuid(), order.Id(), "X", _b1, null, _f.Clock.Now.UtcDateTime));

    // ------------------------------------------------------------------ no-show
    [Fact]
    public async Task A_paid_pickup_order_nobody_collected_is_closed_as_a_no_show_with_the_policy_refund()
    {
        var order = await PickupAsync();
        var paymentId = Guid.NewGuid();
        await _f.HandleAsync(Paid(order, paymentId));
        await BarStartsAsync(order);
        await BarFinishesAsync(order);

        _f.Clock.Now = new DateTimeOffset(Slot().AddMinutes(10));                                         // the slot has passed but the 30-minute wait has not
        await _cashier.Post($"{Orders}/{order.Id()}/no-show", new { reason = "Khách không đến" }).ShouldBe(422, "order.no_show_too_early");
        _payments.Refunds.Should().BeEmpty();

        _f.Clock.Now = new DateTimeOffset(Slot().AddMinutes(31));
        var closed = await (await _cashier.Post($"{Orders}/{order.Id()}/no-show", new { reason = "Khách không đến" })).Body();

        (closed.GetProperty("status").GetString(), closed.GetProperty("refundStatus").GetString()).Should().Be(("NoShow", "RefundPending"));
        closed.GetProperty("cancelledReason").GetString().Should().Be("No-show: Khách không đến");
        _payments.Refunds.Should().ContainSingle().Which.Should().Be((paymentId, "NoShow", (decimal?)null, "Khách không đến"));
        var cancelled = _f.Events.Published.OfType<OrderCancelledIntegrationEvent>().Single();
        (cancelled.OrderId, cancelled.WasPaid, cancelled.CustomerId).Should().Be((order.Id(), true, _customerId));
        (await _manager.GetFromJsonAsync<JsonElement>($"{Orders}/exceptions")).EnumerateArray().Select(o => o.Id()).Should().Contain(order.Id());
    }

    [Fact]
    public async Task When_the_brand_policy_refunds_nothing_for_a_no_show_the_order_is_still_closed()
    {
        var order = await PickupAsync();
        await _f.HandleAsync(Paid(order));
        _payments.Fail = new BusinessRuleException("refund.nothing_to_refund", "The brand policy refunds nothing for this case.");
        _f.Clock.Now = new DateTimeOffset(Slot().AddMinutes(31));

        var closed = await (await _manager.Post($"{Orders}/{order.Id()}/no-show", new { reason = "Khách không đến" })).Body();

        (closed.GetProperty("status").GetString(), closed.GetProperty("refundStatus").GetString()).Should().Be(("NoShow", "None"));
        (await _manager.GetFromJsonAsync<JsonElement>($"{Orders}/exceptions")).EnumerateArray().Select(o => o.Id()).Should().NotContain(order.Id());
    }

    [Fact]
    public async Task Another_payment_problem_leaves_the_order_open()
    {
        var order = await PickupAsync();
        await _f.HandleAsync(Paid(order));
        _payments.Fail = new DependencyUnavailableException("payment");
        _f.Clock.Now = new DateTimeOffset(Slot().AddMinutes(31));

        await _cashier.Post($"{Orders}/{order.Id()}/no-show", new { reason = "x" }).ShouldBe(503);

        (await GetAsync(order.Id())).GetProperty("status").GetString().Should().Be("Confirmed");
    }

    [Fact]
    public async Task An_unpaid_counter_order_left_behind_is_closed_without_a_refund_and_waits_from_the_moment_it_was_ready()
    {
        var order = await CounterOrderAsync();
        await BarStartsAsync(order);
        _f.Clock.Now = Now.AddMinutes(10);
        await BarFinishesAsync(order);                                                                 // ready at 10:10 local

        _f.Clock.Now = Now.AddMinutes(30);
        await _cashier.Post($"{Orders}/{order.Id()}/no-show", new { reason = "Bỏ quên" }).ShouldBe(422, "order.no_show_too_early");
        _f.Clock.Now = Now.AddMinutes(41);
        var closed = await (await _cashier.Post($"{Orders}/{order.Id()}/no-show", new { reason = "Bỏ quên" })).Body();

        (closed.GetProperty("status").GetString(), closed.GetProperty("refundStatus").GetString()).Should().Be(("NoShow", "None"));
        _payments.Refunds.Should().BeEmpty();
    }

    [Fact]
    public async Task A_no_show_is_refused_for_a_handed_over_or_closed_order_and_for_the_wrong_people()
    {
        var delivered = await CounterOrderAsync();
        await _f.HandleAsync(Paid(delivered));
        await BarFinishesAsync(delivered);
        await _cashier.Post($"{Orders}/{delivered.Id()}/deliver", new { }).ShouldBe(200);
        var open = await PickupAsync();
        _f.Clock.Now = new DateTimeOffset(Slot().AddMinutes(40));

        await _cashier.Post($"{Orders}/{delivered.Id()}/no-show", new { reason = "x" }).ShouldBe(422, "order.closed");
        await _cashier.Post($"{Orders}/{open.Id()}/no-show", new { }).ShouldBe(400);
        await _barista.Post($"{Orders}/{open.Id()}/no-show", new { reason = "x" }).ShouldBe(403);
        await _customer.Post($"{Orders}/{open.Id()}/no-show", new { reason = "x" }).ShouldBe(403);
        await _f.As(AicosRoles.Cashier, _b2).Post($"{Orders}/{open.Id()}/no-show", new { reason = "x" }).ShouldBe(403, "branch.forbidden");

        await _cashier.Post($"{Orders}/{open.Id()}/no-show", new { reason = "Không đến" }).ShouldBe(200);
        await _cashier.Post($"{Orders}/{open.Id()}/no-show", new { reason = "Không đến" }).ShouldBe(409, "order.already_cancelled");
    }

    [Fact]
    public async Task A_payment_that_arrives_after_the_no_show_is_marked_for_refund_and_earns_nothing()
    {
        var order = await PickupAsync();
        _f.Clock.Now = new DateTimeOffset(Slot().AddMinutes(31));
        await _cashier.Post($"{Orders}/{order.Id()}/no-show", new { reason = "Không đến" }).ShouldBe(200);

        await _f.HandleAsync(Paid(order));

        (await GetAsync(order.Id())).GetProperty("refundStatus").GetString().Should().Be("Needed");
        _f.Events.Published.OfType<OrderPaidIntegrationEvent>().Should().BeEmpty();
    }

    // ------------------------------------------------------------------ unfulfillable
    [Fact]
    public async Task The_bar_gives_up_a_paid_order_it_cannot_make_and_the_customer_gets_everything_back()
    {
        var order = await PickupAsync();
        var paymentId = Guid.NewGuid();
        await _f.HandleAsync(Paid(order, paymentId));
        await BarStartsAsync(order);

        var closed = await (await _barista.Post($"{Orders}/{order.Id()}/unfulfillable", new { reason = "Hết sữa" })).Body();

        (closed.GetProperty("status").GetString(), closed.GetProperty("refundStatus").GetString()).Should().Be(("Unfulfillable", "RefundPending"));
        closed.GetProperty("cancelledReason").GetString().Should().Be("Unfulfillable: Hết sữa");
        _payments.Refunds.Should().ContainSingle().Which.Should().Be((paymentId, "Unfulfillable", (decimal?)null, "Hết sữa"));
        var cancelled = _f.Events.Published.OfType<OrderCancelledIntegrationEvent>().Single();
        (cancelled.WasPaid, cancelled.OrderId).Should().Be((true, order.Id()));
        await _barista.Post($"{Orders}/{order.Id()}/unfulfillable", new { reason = "again" }).ShouldBe(409, "order.already_cancelled");
    }

    [Fact]
    public async Task An_unpaid_order_the_bar_cannot_make_is_closed_and_its_voucher_is_given_back()
    {
        _vouchers.Percent["SAVE10"] = 10;
        var order = await PickupAsync("SAVE10");

        await _manager.Post($"{Orders}/{order.Id()}/unfulfillable", new { reason = "Máy hỏng" }).ShouldBe(200);

        _payments.Refunds.Should().BeEmpty();
        var cancelled = _f.Events.Published.OfType<OrderCancelledIntegrationEvent>().Single();
        (cancelled.WasPaid, cancelled.PromotionCode).Should().Be((false, "SAVE10"));
        (await GetAsync(order.Id())).GetProperty("refundStatus").GetString().Should().Be("None");
    }

    [Fact]
    public async Task An_order_that_is_already_made_or_handed_over_is_not_unfulfillable()
    {
        var ready = await CounterOrderAsync();
        await _f.HandleAsync(Paid(ready));
        await BarFinishesAsync(ready);
        await _barista.Post($"{Orders}/{ready.Id()}/unfulfillable", new { reason = "x" }).ShouldBe(422, "order.cannot_be_unfulfillable");

        await _cashier.Post($"{Orders}/{ready.Id()}/deliver", new { }).ShouldBe(200);
        await _barista.Post($"{Orders}/{ready.Id()}/unfulfillable", new { reason = "x" }).ShouldBe(422, "order.cannot_be_unfulfillable");
        _payments.Refunds.Should().BeEmpty();
    }

    [Fact]
    public async Task When_the_payment_service_refuses_the_full_refund_the_order_stays_open()
    {
        var order = await PickupAsync();
        await _f.HandleAsync(Paid(order));
        _payments.Fail = new DependencyUnavailableException("payment");

        await _barista.Post($"{Orders}/{order.Id()}/unfulfillable", new { reason = "x" }).ShouldBe(503);

        (await GetAsync(order.Id())).GetProperty("status").GetString().Should().Be("Confirmed");
    }

    [Fact]
    public async Task Only_the_bar_team_and_the_counter_of_the_branch_give_up_an_order()
    {
        var order = await PickupAsync();

        await _barista.Post($"{Orders}/{order.Id()}/unfulfillable", new { }).ShouldBe(400);
        await _customer.Post($"{Orders}/{order.Id()}/unfulfillable", new { reason = "x" }).ShouldBe(403);
        await _f.As(AicosRoles.Barista, _b2).Post($"{Orders}/{order.Id()}/unfulfillable", new { reason = "x" }).ShouldBe(403, "branch.forbidden");
        await _f.As(AicosRoles.Barista, _b1, tenantId: Guid.NewGuid()).Post($"{Orders}/{order.Id()}/unfulfillable", new { reason = "x" }).ShouldBe(404);
        await _f.CreateClient().Post($"{Orders}/{order.Id()}/unfulfillable", new { reason = "x" }).ShouldBe(401);
    }

    [Fact]
    public async Task A_closed_pickup_order_gives_its_place_in_the_slot_back()
    {
        await _manager.Put($"{Orders}/pickup-slots", new { branchId = _b1, slotMinutes = 15, capacityPerSlot = 1, cutoffMinutes = 15, advanceDays = 1 }).ShouldBe(201);
        var first = await PickupAsync();
        await _customer.Post($"{Orders}/pickup", new { branchId = _b1, slotStartUtc = Slot(), items = new object[] { new { productId = _latte, quantity = 1 } } }).ShouldBe(409, "pickup.slot_full");

        await _manager.Post($"{Orders}/{first.Id()}/unfulfillable", new { reason = "Hết ly" }).ShouldBe(200);

        await _customer.Post($"{Orders}/pickup", new { branchId = _b1, slotStartUtc = Slot(), items = new object[] { new { productId = _latte, quantity = 1 } } }).ShouldBe(201);
    }

    // ------------------------------------------------------------------ remake
    [Fact]
    public async Task A_remade_order_is_preparing_again_until_the_bar_finishes_it_once_more()
    {
        var order = await CounterOrderAsync();
        await _f.HandleAsync(Paid(order));
        await BarStartsAsync(order);
        await BarFinishesAsync(order);
        (await GetAsync(order.Id())).GetProperty("status").GetString().Should().Be("Ready");

        var remake = new KitchenOrderRemakeIntegrationEvent(_tenant, Guid.NewGuid(), order.Id(), _b1, "Đổ ly", 1, DateTime.UtcNow);
        await _f.HandleAsync(remake);
        await _f.HandleAsync(remake);                                                                  // delivered twice: nothing more happens

        var preparing = await GetAsync(order.Id());
        (preparing.GetProperty("status").GetString(), preparing.GetProperty("remakeCount").GetInt32()).Should().Be(("Preparing", 1));
        preparing.GetProperty("readyAtUtc").ValueKind.Should().Be(JsonValueKind.Null);
        await _cashier.Post($"{Orders}/{order.Id()}/deliver", new { }).ShouldBe(422, "order.not_ready");     // the cashier cannot hand it over meanwhile

        await BarFinishesAsync(order);
        (await GetAsync(order.Id())).GetProperty("status").GetString().Should().Be("Ready");
        await _cashier.Post($"{Orders}/{order.Id()}/deliver", new { }).ShouldBe(200);
    }

    [Fact]
    public async Task A_late_remake_message_does_not_reopen_a_handed_over_order()
    {
        var order = await CounterOrderAsync();
        await _f.HandleAsync(Paid(order));
        await BarFinishesAsync(order);
        await _cashier.Post($"{Orders}/{order.Id()}/deliver", new { }).ShouldBe(200);

        await _f.HandleAsync(new KitchenOrderRemakeIntegrationEvent(_tenant, Guid.NewGuid(), order.Id(), _b1, "x", 1, DateTime.UtcNow));

        (await GetAsync(order.Id())).GetProperty("status").GetString().Should().Be("Completed");
    }
}
