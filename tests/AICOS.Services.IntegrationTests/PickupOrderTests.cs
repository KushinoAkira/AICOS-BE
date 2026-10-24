using System.Net.Http.Json;
using System.Text.Json;
using AICOS.BuildingBlocks.Security;
using AICOS.Contracts.Events;
using AICOS.Ordering.Api;
using AICOS.Ordering.Api.Features.Orders;
using AICOS.Ordering.Api.Persistence;
using AICOS.Ordering.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AICOS.Services.IntegrationTests;

/// <summary>
/// A customer orders takeaway for a pickup slot (FR-17, UC-21, BR-22 to BR-24), cancels it by themselves, and rates it. The shop clock is
/// Monday 26 Oct 2026 10:00 local (03:00 UTC); the default rules are 15-minute slots, 10 orders per slot, locked 15 minutes before.
/// </summary>
public sealed class PickupOrderTests : IDisposable
{
    private const string Orders = "/api/v1/orders";
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 3, 0, 0, TimeSpan.Zero);

    private readonly FakeMenu _menu = new();
    private readonly FakeVouchers _vouchers = new();
    private readonly FakePayments _payments = new();
    private readonly ServiceFactory<OrderingService, OrderingDbContext> _f;
    private readonly Guid _tenant = ServiceFactory<OrderingService, OrderingDbContext>.DefaultTenantId;
    private readonly Guid _otherTenant = Guid.NewGuid();
    private readonly Guid _b1;
    private readonly Guid _foreign;
    private readonly Guid _latte;
    private readonly Guid _customerId = Guid.NewGuid();
    private readonly HttpClient _customer;
    private readonly HttpClient _otherCustomer;
    private readonly HttpClient _cashier;
    private readonly HttpClient _manager;
    private readonly HttpClient _admin;
    private readonly HttpClient _otherManager;

    public PickupOrderTests()
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
        _foreign = _f.Branches.Add(_otherTenant, name: "Foreign");
        _latte = _menu.Add(_b1, "Latte", 55_000);
        _customer = _f.As(AicosRoles.Customer, userId: _customerId);
        _otherCustomer = _f.As(AicosRoles.Customer, userId: Guid.NewGuid());
        _cashier = _f.As(AicosRoles.Cashier, _b1);
        _manager = _f.As(AicosRoles.BranchManager, _b1);
        _admin = _f.As(AicosRoles.BrandAdmin);
        _otherManager = _f.As(AicosRoles.BranchManager, _foreign, tenantId: _otherTenant);
    }

    public void Dispose() => _f.Dispose();

    /// <summary>Local 11:00 on Monday = 04:00 UTC: inside the pickup hours, an hour after "now".</summary>
    private static DateTime Slot(int localHour = 11, int minute = 0, int addDays = 0) =>
        new DateTime(2026, 10, 26, 0, 0, 0, DateTimeKind.Utc).AddDays(addDays).AddHours(localHour - 7).AddMinutes(minute);

    private object Body(DateTime? slot = null, string? promo = null, Guid? branch = null) => new
    {
        branchId = branch ?? _b1,
        slotStartUtc = slot ?? Slot(),
        promotionCode = promo,
        items = new object[] { new { productId = _latte, quantity = 2, note = "ít đá" } },
    };

    private Task<HttpResponseMessage> PlaceAsync(HttpClient? client = null, DateTime? slot = null, string? promo = null, string? key = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"{Orders}/pickup") { Content = JsonContent.Create(Body(slot, promo), options: Http.Json) };
        if (key is not null)
        {
            request.Headers.Add("Idempotency-Key", key);
        }

        return (client ?? _customer).SendAsync(request);
    }

    private async Task<JsonElement> PlaceOkAsync(HttpClient? client = null, DateTime? slot = null, string? promo = null) =>
        await (await PlaceAsync(client, slot, promo)).Body();

    private PaymentSucceededEvent Paid(JsonElement order, Guid? paymentId = null) => new(
        _tenant, _b1, paymentId ?? Guid.NewGuid(), order.Id(), order.GetProperty("finalAmount").GetDecimal(), "Mock", _f.Clock.Now.UtcDateTime);

    private async Task SetCapacityAsync(int capacity) =>
        (await _admin.Put($"{Orders}/pickup-slots", new { branchId = _b1, slotMinutes = 15, capacityPerSlot = capacity, cutoffMinutes = 15, advanceDays = 1 })).IsSuccessStatusCode.Should().BeTrue();

    private async Task<JsonElement> GetAsync(Guid id, HttpClient? client = null) => await (client ?? _manager).GetFromJsonAsync<JsonElement>($"{Orders}/{id}");

    // ------------------------------------------------------------------ the slots
    [Fact]
    public async Task A_customer_sees_the_slots_of_the_day_and_which_are_locked_or_full()
    {
        await SetCapacityAsync(1);
        await PlaceOkAsync(slot: Slot(11));

        var day = await _customer.GetFromJsonAsync<JsonElement>($"{Orders}/pickup-slots/available?branchId={_b1}");

        day.GetProperty("slotMinutes").GetInt32().Should().Be(15);
        var slots = day.GetProperty("slots").EnumerateArray().ToList();
        slots.Should().HaveCount(60);                                                          // 07:00 to 22:00
        slots.First().GetProperty("startUtc").GetDateTime().Should().Be(Slot(7));
        slots.Where(s => s.GetProperty("locked").GetBoolean()).Max(s => s.GetProperty("startUtc").GetDateTime()).Should().Be(Slot(10, 15));   // 10:00 now, cut-off 15 minutes
        var taken = slots.Single(s => s.GetProperty("startUtc").GetDateTime() == Slot(11));
        (taken.GetProperty("booked").GetInt32(), taken.GetProperty("available").GetBoolean()).Should().Be((1, false));
        slots.Single(s => s.GetProperty("startUtc").GetDateTime() == Slot(11, 15)).GetProperty("available").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Slots_outside_the_advance_window_are_not_offered_and_another_brands_branch_is_refused()
    {
        var farDay = await _customer.GetFromJsonAsync<JsonElement>($"{Orders}/pickup-slots/available?branchId={_b1}&date=2026-11-20");
        farDay.GetProperty("slots").GetArrayLength().Should().Be(0);

        await _customer.GetAsync($"{Orders}/pickup-slots/available?branchId={_foreign}").ShouldBe(400);
        await _customer.GetAsync($"{Orders}/pickup-slots/available").ShouldBe(400);                                         // a branch is required
    }

    // ------------------------------------------------------------------ placing the order
    [Fact]
    public async Task A_customer_orders_takeaway_for_a_slot_and_the_bar_is_told_when_to_expect_it()
    {
        var response = await PlaceAsync();

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.Created);
        var order = await response.Body();
        order.GetProperty("type").GetString().Should().Be("Takeaway");
        order.GetProperty("customerId").GetGuid().Should().Be(_customerId);
        order.GetProperty("cashierId").ValueKind.Should().Be(JsonValueKind.Null);
        (order.GetProperty("status").GetString(), order.GetProperty("paymentStatus").GetString()).Should().Be(("Pending", "Unpaid"));
        order.GetProperty("finalAmount").GetDecimal().Should().Be(110_000);                              // 2 x 55,000 from the menu
        order.GetProperty("pickupSlotStartUtc").GetDateTime().Should().Be(Slot());
        order.GetProperty("paymentDueAtUtc").GetDateTime().Should().Be(Now.UtcDateTime.AddMinutes(15));

        var created = _f.Events.Published.OfType<OrderCreatedIntegrationEvent>().Single();
        (created.CustomerId, created.OrderType, created.ScheduledPickupTimeUtc, created.IsPaid).Should().Be((_customerId, "Takeaway", Slot(), false));

        var mine = await _customer.GetFromJsonAsync<JsonElement>($"{Orders}/mine");
        mine.EnumerateArray().Select(o => o.Id()).Should().Contain(order.Id());
    }

    [Fact]
    public async Task Pressing_order_twice_with_the_same_key_gives_back_the_same_order()
    {
        var first = await PlaceAsync(key: "abc-123");
        var second = await PlaceAsync(key: "abc-123");
        var other = await PlaceAsync(_otherCustomer, key: "abc-123");                  // the key belongs to the customer

        first.StatusCode.Should().Be(System.Net.HttpStatusCode.Created);
        second.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        var firstId = (await first.Body()).Id();
        (await second.Body()).Id().Should().Be(firstId);
        (await other.Body()).Id().Should().NotBe(firstId);
        _f.Events.Published.OfType<OrderCreatedIntegrationEvent>().Should().HaveCount(2);
    }

    [Fact]
    public async Task The_slot_must_be_an_offered_one()
    {
        await PlaceAsync(slot: Slot(11, 5)).ShouldBe(422, "pickup.invalid_slot");                       // not on the 15-minute grid
        await PlaceAsync(slot: Slot(6, 45)).ShouldBe(422, "pickup.invalid_slot");                        // before opening
        await PlaceAsync(slot: Slot(22, 0)).ShouldBe(422, "pickup.invalid_slot");                        // the slot would end after closing
        await PlaceAsync(slot: Slot(10, 15)).ShouldBe(422, "pickup.slot_locked");                        // BR-23: 10:15 is inside the 15-minute cut-off
        await PlaceAsync(slot: Slot(11, addDays: 2)).ShouldBe(422, "pickup.too_far_ahead");
        await PlaceAsync(slot: Slot(10, 30)).ShouldBe(201);                                              // the first slot that is still open
    }

    [Fact]
    public async Task A_full_slot_refuses_the_order_and_does_not_use_up_a_voucher()
    {
        await SetCapacityAsync(1);
        _vouchers.Percent["SAVE10"] = 10;
        await PlaceAsync().ShouldBe(201);

        await PlaceAsync(_otherCustomer, promo: "SAVE10").ShouldBe(409, "pickup.slot_full");
        await PlaceAsync(_otherCustomer, slot: Slot(11, 15), promo: "SAVE10").ShouldBe(201);              // the voucher is still good
        _vouchers.Redeemed.Should().ContainSingle().Which.Customer.Should().NotBeNull();
    }

    [Fact]
    public async Task A_cancelled_order_frees_its_place_in_the_slot()
    {
        await SetCapacityAsync(1);
        var first = await PlaceOkAsync();
        await PlaceAsync(_otherCustomer).ShouldBe(409, "pickup.slot_full");

        await _customer.Post($"{Orders}/{first.Id()}/cancel", new { reason = "Đổi ý" }).ShouldBe(200);

        await PlaceAsync(_otherCustomer).ShouldBe(201);
    }

    [Fact]
    public async Task Only_a_customer_places_pickup_orders_and_only_for_a_product_on_sale()
    {
        await _cashier.Post($"{Orders}/pickup", Body()).ShouldBe(403);
        await _f.As(AicosRoles.Customer, userId: Guid.NewGuid()).Post($"{Orders}/pickup", Body(branch: _foreign)).ShouldBe(400);        // a branch of another brand
        await _customer.Post($"{Orders}/pickup", new { branchId = _b1, slotStartUtc = Slot(), items = new object[] { new { productId = Guid.NewGuid(), quantity = 1 } } })
            .ShouldBe(422, "order.item_unavailable");
        await _customer.Post($"{Orders}/pickup", new { branchId = _b1, items = new object[] { new { productId = _latte, quantity = 1 } } }).ShouldBe(400);   // no slot
        await _customer.Post($"{Orders}/pickup", new { branchId = _b1, slotStartUtc = Slot(), items = Array.Empty<object>() }).ShouldBe(400);
    }

    [Fact]
    public async Task A_voucher_of_a_member_is_used_on_the_order_and_comes_back_when_the_order_is_not_paid_in_time()
    {
        _vouchers.Percent["SAVE10"] = 10;
        var order = await PlaceOkAsync(promo: "SAVE10");
        order.GetProperty("discountAmount").GetDecimal().Should().Be(11_000);
        _vouchers.Redeemed.Single().Customer.Should().Be(_customerId);

        _f.Clock.Now = Now.AddMinutes(16);
        using (var scope = _f.Services.CreateScope())
        {
            (await PickupOrderExpiryWorker.ReleaseOverdueAsync(scope.ServiceProvider, _f.Clock.Now.UtcDateTime, default)).Should().Be(1);
        }

        (await GetAsync(order.Id())).GetProperty("status").GetString().Should().Be("Cancelled");
        var cancelled = _f.Events.Published.OfType<OrderCancelledIntegrationEvent>().Single();
        (cancelled.WasPaid, cancelled.PromotionCode, cancelled.CustomerId).Should().Be((false, "SAVE10", _customerId));
    }

    [Fact]
    public async Task A_paid_pickup_order_is_never_released_by_the_timer()
    {
        var order = await PlaceOkAsync();
        await _f.HandleAsync(Paid(order));

        _f.Clock.Now = Now.AddHours(1);
        using var scope = _f.Services.CreateScope();
        (await PickupOrderExpiryWorker.ReleaseOverdueAsync(scope.ServiceProvider, _f.Clock.Now.UtcDateTime, default)).Should().Be(0);
        (await GetAsync(order.Id())).GetProperty("status").GetString().Should().Be("Confirmed");
    }

    // ------------------------------------------------------------------ the customer cancels
    [Fact]
    public async Task A_customer_cancels_an_unpaid_order_by_themselves()
    {
        var order = await PlaceOkAsync();

        var cancelled = await (await _customer.Post($"{Orders}/{order.Id()}/cancel", new { reason = "Đổi ý" })).Body();

        (cancelled.GetProperty("status").GetString(), cancelled.GetProperty("cancelledReason").GetString()).Should().Be(("Cancelled", "Đổi ý"));
        _f.Events.Published.OfType<OrderCancelledIntegrationEvent>().Single().WasPaid.Should().BeFalse();
        _payments.Refunds.Should().BeEmpty();
        await _customer.Post($"{Orders}/{order.Id()}/cancel", new { reason = "again" }).ShouldBe(409, "order.already_cancelled");
    }

    [Fact]
    public async Task A_customer_who_cancels_a_paid_order_gets_the_refund_the_brand_policy_gives()
    {
        var order = await PlaceOkAsync();
        var paymentId = Guid.NewGuid();
        await _f.HandleAsync(Paid(order, paymentId));

        var cancelled = await (await _customer.Post($"{Orders}/{order.Id()}/cancel", new { reason = "Bận việc" })).Body();

        cancelled.GetProperty("status").GetString().Should().Be("Cancelled");
        cancelled.GetProperty("refundStatus").GetString().Should().Be("RefundPending");
        _payments.Refunds.Should().ContainSingle().Which.Should().Be((paymentId, "Cancellation", (decimal?)null, "Bận việc"));
        _f.Events.Published.OfType<OrderCancelledIntegrationEvent>().Single().WasPaid.Should().BeTrue();
        (await _manager.GetFromJsonAsync<JsonElement>($"{Orders}/exceptions")).EnumerateArray().Select(o => o.Id()).Should().Contain(order.Id());
    }

    [Fact]
    public async Task When_the_payment_service_refuses_the_refund_the_order_stays_as_it_was()
    {
        var order = await PlaceOkAsync();
        await _f.HandleAsync(Paid(order));
        _payments.Fail = new AICOS.BuildingBlocks.Exceptions.BusinessRuleException("refund.nothing_to_refund", "The brand policy refunds nothing.");

        await _customer.Post($"{Orders}/{order.Id()}/cancel", new { reason = "x" }).ShouldBe(422, "refund.nothing_to_refund");

        (await GetAsync(order.Id())).GetProperty("status").GetString().Should().Be("Confirmed");
    }

    [Fact]
    public async Task A_customer_cannot_cancel_once_the_slot_is_locked_or_the_bar_has_started()
    {
        var order = await PlaceOkAsync(slot: Slot(11));
        var started = await PlaceOkAsync(slot: Slot(12));
        await _f.HandleAsync(Paid(order));
        await _f.HandleAsync(Paid(started));
        await _f.HandleAsync(new KitchenOrderStartedIntegrationEvent(_tenant, Guid.NewGuid(), started.Id(), _b1, DateTime.UtcNow));

        await _customer.Post($"{Orders}/{started.Id()}/cancel", new { }).ShouldBe(422, "order.cannot_cancel_now");

        _f.Clock.Now = Now.AddMinutes(50);                                                              // 10:50, the 11:00 slot is locked from 10:45
        await _customer.Post($"{Orders}/{order.Id()}/cancel", new { }).ShouldBe(422, "order.pickup_locked");
        _payments.Refunds.Should().BeEmpty();                                                           // no money moved on a refusal
    }

    [Fact]
    public async Task A_customer_cancels_only_their_own_order()
    {
        var order = await PlaceOkAsync();

        await _otherCustomer.Post($"{Orders}/{order.Id()}/cancel", new { }).ShouldBe(404);
        await _f.As(AicosRoles.Barista, _b1).Post($"{Orders}/{order.Id()}/cancel", new { }).ShouldBe(403);
        await _cashier.Post($"{Orders}/{order.Id()}/cancel", new { reason = "counter" }).ShouldBe(200);   // the counter keeps its own way
    }

    // ------------------------------------------------------------------ feedback
    private async Task<JsonElement> CompletedOrderAsync()
    {
        var order = await PlaceOkAsync();
        await _f.HandleAsync(Paid(order));
        await _f.HandleAsync(new KitchenOrderReadyIntegrationEvent(_tenant, Guid.NewGuid(), order.Id(), "X", _b1, null, DateTime.UtcNow));
        await _cashier.Post($"{Orders}/{order.Id()}/deliver", new { }).ShouldBe(200);
        return order;
    }

    [Fact]
    public async Task A_customer_rates_a_received_order_once()
    {
        var pending = await PlaceOkAsync();
        await _customer.Post($"{Orders}/{pending.Id()}/feedback", new { rating = 5 }).ShouldBe(422, "feedback.order_not_completed");

        var order = await CompletedOrderAsync();
        var response = await _customer.Post($"{Orders}/{order.Id()}/feedback", new { rating = 4, comment = "  Ngon, hơi lâu  " });

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.Created);
        var feedback = await response.Body();
        (feedback.GetProperty("rating").GetInt32(), feedback.GetProperty("comment").GetString()).Should().Be((4, "Ngon, hơi lâu"));
        feedback.GetProperty("customerId").GetGuid().Should().Be(_customerId);
        (await _customer.GetFromJsonAsync<JsonElement>($"{Orders}/{order.Id()}/feedback")).GetProperty("rating").GetInt32().Should().Be(4);

        await _customer.Post($"{Orders}/{order.Id()}/feedback", new { rating = 1 }).ShouldBe(409, "feedback.duplicate");
    }

    [Fact]
    public async Task Feedback_is_validated_and_belongs_to_the_customer_of_the_order()
    {
        var order = await CompletedOrderAsync();

        await _customer.Post($"{Orders}/{order.Id()}/feedback", new { rating = 0 }).ShouldBe(400);
        await _customer.Post($"{Orders}/{order.Id()}/feedback", new { rating = 6 }).ShouldBe(400);
        await _customer.Post($"{Orders}/{order.Id()}/feedback", new { comment = "no rating" }).ShouldBe(400);
        await _customer.Post($"{Orders}/{order.Id()}/feedback", new { rating = 5, comment = new string('x', 501) }).ShouldBe(400);
        await _otherCustomer.Post($"{Orders}/{order.Id()}/feedback", new { rating = 5 }).ShouldBe(404);
        await _cashier.Post($"{Orders}/{order.Id()}/feedback", new { rating = 5 }).ShouldBe(403);
        await _customer.GetAsync($"{Orders}/{order.Id()}/feedback").ShouldBe(404);                           // nothing written yet
    }

    [Fact]
    public async Task Managers_read_the_ratings_of_their_scope()
    {
        var good = await CompletedOrderAsync();
        var bad = await CompletedOrderAsync();
        await _customer.Post($"{Orders}/{good.Id()}/feedback", new { rating = 5, comment = "Tuyệt" }).ShouldBe(201);
        await _customer.Post($"{Orders}/{bad.Id()}/feedback", new { rating = 2, comment = "Chậm" }).ShouldBe(201);

        var all = await _manager.GetFromJsonAsync<JsonElement>($"{Orders}/feedbacks");
        all.GetArrayLength().Should().Be(2);
        var low = await _admin.GetFromJsonAsync<JsonElement>($"{Orders}/feedbacks?maxRating=3");
        low.EnumerateArray().Single().GetProperty("comment").GetString().Should().Be("Chậm");

        (await _otherManager.GetFromJsonAsync<JsonElement>($"{Orders}/feedbacks")).GetArrayLength().Should().Be(0);   // another brand sees none
        await _customer.GetAsync($"{Orders}/feedbacks").ShouldBe(403);
        await _cashier.GetAsync($"{Orders}/feedbacks").ShouldBe(403);
    }
}
