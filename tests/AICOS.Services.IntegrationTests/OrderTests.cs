using System.Net.Http.Json;
using System.Text.Json;
using AICOS.BuildingBlocks.Exceptions;
using AICOS.BuildingBlocks.Security;
using AICOS.Contracts.Events;
using AICOS.Ordering.Api;
using AICOS.Ordering.Api.Persistence;
using AICOS.Ordering.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AICOS.Services.IntegrationTests;

/// <summary>Stands in for the Menu service: which products a branch sells, and at which price.</summary>
public sealed class FakeMenu : IMenuCatalogClient
{
    private readonly Dictionary<Guid, Dictionary<Guid, MenuProductInfo>> _byBranch = [];

    public Guid Add(Guid branchId, string name, decimal price)
    {
        var id = Guid.NewGuid();
        if (!_byBranch.TryGetValue(branchId, out var products))
        {
            _byBranch[branchId] = products = [];
        }

        products[id] = new MenuProductInfo(id, name, price);
        return id;
    }

    public void SoldOut(Guid branchId, Guid productId) => _byBranch[branchId].Remove(productId);

    /// <summary>Gives a product option groups, as the Menu service would list them.</summary>
    public void SetOptions(Guid branchId, Guid productId, params MenuOptionGroupInfo[] groups) =>
        _byBranch[branchId][productId] = _byBranch[branchId][productId] with { OptionGroups = groups };

    public Task<IReadOnlyDictionary<Guid, MenuProductInfo>> GetSellableProductsAsync(Guid branchId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyDictionary<Guid, MenuProductInfo>>(_byBranch.TryGetValue(branchId, out var p) ? new Dictionary<Guid, MenuProductInfo>(p) : []);
}

/// <summary>Stands in for the Customer service vouchers.</summary>
public sealed class FakeVouchers : IVoucherClient
{
    public Dictionary<string, decimal> Percent { get; } = [];

    public List<(string Code, decimal Amount, string Reference, Guid Branch, Guid? Customer)> Redeemed { get; } = [];

    public Task<VoucherQuote> RedeemAsync(string code, decimal orderAmount, string orderReference, Guid branchId, Guid? customerId, CancellationToken ct)
    {
        if (!Percent.TryGetValue(code, out var percent) || Redeemed.Any(r => r.Code == code))
        {
            throw new BusinessRuleException("voucher.invalid", "The voucher is not valid.");
        }

        Redeemed.Add((code, orderAmount, orderReference, branchId, customerId));
        return Task.FromResult(new VoucherQuote(code, Math.Round(orderAmount * percent / 100)));
    }
}

/// <summary>Stands in for the Payment service refund request.</summary>
public sealed class FakePayments : IPaymentClient
{
    public List<(Guid PaymentId, string Kind, decimal? Amount, string Reason)> Refunds { get; } = [];

    public Exception? Fail { get; set; }

    /// <summary>The waiting time of the brand policy before a no-show may be recorded.</summary>
    public int NoShowAfterMinutes { get; set; } = 30;

    public Task<int> GetNoShowAfterMinutesAsync(CancellationToken ct) => Task.FromResult(NoShowAfterMinutes);

    /// <summary>Runs while the request is open, like a refund event that arrives before the answer does.</summary>
    public Func<Task>? WhileOpen { get; set; }

    public Task<RefundRequestResult> RequestRefundAsync(Guid paymentId, string kind, decimal? amount, string reason, CancellationToken ct)
    {
        if (Fail is not null)
        {
            throw Fail;
        }

        Refunds.Add((paymentId, kind, amount, reason));
        WhileOpen?.Invoke().GetAwaiter().GetResult();
        return Task.FromResult(new RefundRequestResult("Succeeded"));
    }
}

/// <summary>Counter orders: prices from the menu, tenant and branch rules, the events from Payment and Kitchen, delivery and the manager exception path.</summary>
public sealed class OrderTests : IDisposable
{
    private const string Orders = "/api/v1/orders";

    private readonly FakeMenu _menu = new();
    private readonly FakeVouchers _vouchers = new();
    private readonly FakePayments _payments = new();
    private readonly ServiceFactory<OrderingService, OrderingDbContext> _f;
    private readonly Guid _tenant = ServiceFactory<OrderingService, OrderingDbContext>.DefaultTenantId;
    private readonly Guid _otherTenant = Guid.NewGuid();
    private readonly Guid _b1;
    private readonly Guid _b2;
    private readonly Guid _foreign;
    private readonly Guid _cashierId = Guid.NewGuid();
    private readonly Guid _managerId = Guid.NewGuid();
    private readonly Guid _customerId = Guid.NewGuid();
    private readonly Guid _latte;
    private readonly Guid _bacXiu;
    private readonly HttpClient _cashier;
    private readonly HttpClient _manager;
    private readonly HttpClient _admin;
    private readonly HttpClient _otherAdmin;
    private readonly HttpClient _customer;

    public OrderTests()
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
        _b1 = _f.Branches.Add(_tenant, name: "Quận 1");
        _b2 = _f.Branches.Add(_tenant, name: "Quận 3");
        _foreign = _f.Branches.Add(_otherTenant, name: "Foreign");
        _latte = _menu.Add(_b1, "Latte", 55_000);
        _bacXiu = _menu.Add(_b1, "Bạc xỉu", 29_000);
        _cashier = _f.As(AicosRoles.Cashier, _b1, _cashierId);
        _manager = _f.As(AicosRoles.BranchManager, _b1, _managerId);
        _admin = _f.As(AicosRoles.BrandAdmin);
        _otherAdmin = _f.As(AicosRoles.BrandAdmin, tenantId: _otherTenant);
        _customer = _f.As(AicosRoles.Customer, userId: _customerId);
    }

    public void Dispose() => _f.Dispose();

    private object Body(Guid? branchId = null, string type = "Takeaway", object[]? items = null, string? promo = null, Guid? customerId = null, string? address = null) => new
    {
        branchId,
        type,
        customerId,
        deliveryAddress = address,
        promotionCode = promo,
        items = items ?? [new { productId = _latte, quantity = 2 }, new { productId = _bacXiu, quantity = 1, note = "ít đá" }],
    };

    private async Task<JsonElement> NewOrderAsync(HttpClient? client = null, string type = "Takeaway", Guid? customerId = null) =>
        await (client ?? _cashier).PostOk(Orders, Body(type: type, customerId: customerId));

    private PaymentSucceededEvent Paid(JsonElement order, string method = "Cash", Guid? paymentId = null, Guid? tenant = null) => new(
        tenant ?? _tenant, _b1, paymentId ?? Guid.NewGuid(), order.Id(), order.GetProperty("finalAmount").GetDecimal(), method, _f.Clock.Now.UtcDateTime);

    private async Task<JsonElement> GetAsync(Guid id, HttpClient? client = null) => await (client ?? _manager).GetFromJsonAsync<JsonElement>($"{Orders}/{id}");

    [Fact]
    public async Task A_refund_that_crossed_the_cancellation_is_shown_as_refunded()
    {
        var order = await NewOrderAsync(customerId: _customerId);
        await _f.HandleAsync(Paid(order, paymentId: Guid.NewGuid()));
        await _manager.Post($"{Orders}/{order.Id()}/exception", new { reason = "Complaint", refundKind = "Cancellation" }).ShouldBe(200);
        await _f.HandleAsync(new PaymentRefundedEvent(_tenant, _b1, Guid.NewGuid(), Guid.NewGuid(), order.Id(), 139_000, 139_000, "Mock"));

        // The two writes (cancel, refund) touch different columns and can leave the stored state at RefundPending although the money went back.
        using (var scope = _f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
            var row = await db.Orders.FirstAsync(o => o.Id == order.Id());
            typeof(AICOS.Ordering.Api.Domain.Entities.Order).GetProperty("RefundStatus")!.SetValue(row, AICOS.Ordering.Api.Domain.Enums.RefundStatus.RefundPending);
            await db.SaveChangesAsync();
        }

        (await GetAsync(order.Id())).GetProperty("refundStatus").GetString().Should().Be("Refunded");
        var listed = await _manager.GetFromJsonAsync<JsonElement>($"{Orders}/exceptions?refundStatus=Refunded");
        listed.EnumerateArray().Select(o => o.Id()).Should().Contain(order.Id());
    }

    // ------------------------------------------------------------------ create
    [Fact]
    public async Task A_counter_order_takes_name_and_price_from_the_menu_not_from_the_request()
    {
        var response = await _cashier.Post(Orders, new
        {
            type = "Takeaway",
            items = new object[] { new { productId = _latte, quantity = 2, productName = "Hacked", unitPrice = 1 }, new { productId = _bacXiu, quantity = 1, note = "ít đá" } },
        });

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.Created);
        var order = await response.Body();
        order.GetProperty("branchId").GetGuid().Should().Be(_b1);                          // a cashier works at the branch of the token
        order.GetProperty("cashierId").GetGuid().Should().Be(_cashierId);
        order.GetProperty("status").GetString().Should().Be("Pending");
        order.GetProperty("paymentStatus").GetString().Should().Be("Unpaid");
        order.GetProperty("originalAmount").GetDecimal().Should().Be(139_000);             // 2 x 55,000 + 29,000
        order.GetProperty("finalAmount").GetDecimal().Should().Be(139_000);
        order.GetProperty("orderCode").GetString().Should().StartWith("ORD-");
        var items = order.GetProperty("items").EnumerateArray().ToList();
        items.Should().HaveCount(2);
        items.Select(i => i.GetProperty("productName").GetString()).Should().BeEquivalentTo("Latte", "Bạc xỉu");
        items.First(i => i.GetProperty("productName").GetString() == "Latte").GetProperty("unitPrice").GetDecimal().Should().Be(55_000);

        var created = _f.Events.Published.OfType<OrderCreatedIntegrationEvent>().Single();
        (created.TenantId, created.OrderId, created.BranchId, created.TotalAmount, created.IsPaid).Should().Be((_tenant, order.Id(), _b1, 139_000m, false));
        created.Items.Should().HaveCount(2);
    }

    [Fact]
    public async Task The_same_product_twice_with_the_same_note_is_one_line()
    {
        var order = await _cashier.PostOk(Orders, Body(items: [new { productId = _latte, quantity = 1 }, new { productId = _latte, quantity = 2 }]));

        var lines = order.GetProperty("items").EnumerateArray().ToList();

        lines.Should().ContainSingle();
        lines[0].GetProperty("quantity").GetInt32().Should().Be(3);
    }

    [Fact]
    public async Task Orders_are_validated()
    {
        await _cashier.Post(Orders, new { }).ShouldBe(400);
        await _cashier.Post(Orders, Body(items: [])).ShouldBe(400);
        await _cashier.Post(Orders, Body(items: [new { productId = _latte, quantity = 0 }])).ShouldBe(400);
        await _cashier.Post(Orders, Body(items: [new { productId = _latte, quantity = 100 }])).ShouldBe(400);
        await _cashier.Post(Orders, Body(items: [new { quantity = 1 }])).ShouldBe(400);
        await _cashier.Post(Orders, Body(items: Enumerable.Range(0, 51).Select(_ => (object)new { productId = _latte, quantity = 1 }).ToArray())).ShouldBe(400);
        await _cashier.Post(Orders, Body(type: "Delivery")).ShouldBe(400);                                                       // an address is required
        await _cashier.Post(Orders, Body(type: "Delivery", address: "1 Lê Lợi")).ShouldBe(201);
        await _cashier.Post(Orders, new { type = "Teleport", items = new[] { new { productId = _latte, quantity = 1 } } }).ShouldBe(400);
    }

    [Fact]
    public async Task A_product_that_is_sold_out_or_unknown_cannot_be_ordered()
    {
        _menu.SoldOut(_b1, _bacXiu);

        await _cashier.Post(Orders, Body()).ShouldBe(422, "order.item_unavailable");
        await _cashier.Post(Orders, Body(items: [new { productId = Guid.NewGuid(), quantity = 1 }])).ShouldBe(422, "order.item_unavailable");
        await _cashier.Post(Orders, Body(items: [new { productId = _latte, quantity = 1 }])).ShouldBe(201);
        _f.Events.Published.OfType<OrderCreatedIntegrationEvent>().Should().ContainSingle();
    }

    [Fact]
    public async Task Who_can_create_an_order_and_at_which_branch()
    {
        await _admin.Post(Orders, Body()).ShouldBe(400);                                               // a brand admin names the branch
        (await _admin.PostOk(Orders, Body(branchId: _b1))).GetProperty("branchId").GetGuid().Should().Be(_b1);
        await _admin.Post(Orders, Body(branchId: _foreign)).ShouldBe(400);                              // a branch of another brand
        await _cashier.Post(Orders, Body(branchId: _b2)).ShouldBe(403, "branch.forbidden");

        await _f.As(AicosRoles.Barista, _b1).Post(Orders, Body()).ShouldBe(403);
        await _customer.Post(Orders, Body(branchId: _b1)).ShouldBe(403);
        await _f.As(AicosRoles.SuperAdmin).Post(Orders, Body(branchId: _b1)).ShouldBe(403);
        await _f.CreateClient().Post(Orders, Body(branchId: _b1)).ShouldBe(401);
        _f.Branches.Suspend(_b1);
        await _admin.Post(Orders, Body(branchId: _b1)).ShouldBe(422, "branch.suspended");
    }

    [Fact]
    public async Task A_voucher_is_redeemed_with_the_order_and_discounts_it()
    {
        _vouchers.Percent["SAVE10"] = 10;

        var order = await _cashier.PostOk(Orders, Body(promo: "SAVE10"));

        order.GetProperty("discountAmount").GetDecimal().Should().Be(13_900);
        order.GetProperty("finalAmount").GetDecimal().Should().Be(125_100);
        order.GetProperty("promotionCode").GetString().Should().Be("SAVE10");
        _vouchers.Redeemed.Should().ContainSingle().Which.Reference.Should().Be(order.GetProperty("orderCode").GetString());
        await _cashier.Post(Orders, Body(promo: "SAVE10")).ShouldBe(422, "voucher.invalid");                  // used up
        await _cashier.Post(Orders, Body(promo: "NOPE")).ShouldBe(422, "voucher.invalid");                    // and nothing is made up
        (await _manager.GetFromJsonAsync<JsonElement>(Orders)).GetArrayLength().Should().Be(1);
    }

    // ------------------------------------------------------------------ read and isolation
    [Fact]
    public async Task Lists_are_scoped_and_filtered()
    {
        var a = await NewOrderAsync();
        var b = await NewOrderAsync();
        await _f.HandleAsync(Paid(b));
        await _admin.PostOk(Orders, Body(branchId: _b1));
        _f.Clock.Now = _f.Clock.Now.AddDays(1);
        await NewOrderAsync();

        (await _manager.GetFromJsonAsync<JsonElement>(Orders)).GetArrayLength().Should().Be(4);
        (await _manager.GetFromJsonAsync<JsonElement>($"{Orders}?paymentStatus=Paid")).GetArrayLength().Should().Be(1);
        (await _manager.GetFromJsonAsync<JsonElement>($"{Orders}?status=Confirmed")).GetArrayLength().Should().Be(1);
        var today = DateOnly.FromDateTime(_f.Clock.Now.UtcDateTime.AddDays(-1).AddHours(7));
        (await _manager.GetFromJsonAsync<JsonElement>($"{Orders}?from={today:yyyy-MM-dd}&to={today:yyyy-MM-dd}")).GetArrayLength().Should().Be(3);
        (await _manager.GetFromJsonAsync<JsonElement>($"{Orders}?limit=2")).GetArrayLength().Should().Be(2);
        (await _admin.GetFromJsonAsync<JsonElement>($"{Orders}?branchId={_b1}")).GetArrayLength().Should().Be(4);
        (await _otherAdmin.GetFromJsonAsync<JsonElement>(Orders)).GetArrayLength().Should().Be(0);
        (await _f.As(AicosRoles.BranchManager, _b2).GetFromJsonAsync<JsonElement>(Orders)).GetArrayLength().Should().Be(0);
        await _manager.GetAsync($"{Orders}?branchId={_b2}").ShouldBe(403, "branch.forbidden");
        await _customer.GetAsync(Orders).ShouldBe(403);
        a.Id().Should().NotBe(b.Id());
    }

    [Fact]
    public async Task One_order_can_only_be_read_by_its_branch_its_brand_and_its_customer()
    {
        var mine = await NewOrderAsync(customerId: _customerId);
        var url = $"{Orders}/{mine.Id()}";

        await _cashier.GetAsync(url).ShouldBe(200);
        await _admin.GetAsync(url).ShouldBe(200);
        await _customer.GetAsync(url).ShouldBe(200);
        await _f.As(AicosRoles.Customer).GetAsync(url).ShouldBe(404);                                       // somebody else's order
        await _f.As(AicosRoles.Cashier, _b2).GetAsync(url).ShouldBe(403, "branch.forbidden");
        await _otherAdmin.GetAsync(url).ShouldBe(404);
        await _f.As(AicosRoles.SuperAdmin).GetAsync(url).ShouldBe(403, "tenant.required");
        await _f.CreateClient().GetAsync(url).ShouldBe(401);
        await _cashier.GetAsync($"{Orders}/{Guid.NewGuid()}").ShouldBe(404);

        (await _customer.GetFromJsonAsync<JsonElement>($"{Orders}/mine")).GetArrayLength().Should().Be(1);
        (await _f.As(AicosRoles.Customer).GetFromJsonAsync<JsonElement>($"{Orders}/mine")).GetArrayLength().Should().Be(0);
        await _cashier.GetAsync($"{Orders}/mine").ShouldBe(403);
    }

    // ------------------------------------------------------------------ cancel (BR-04)
    [Fact]
    public async Task An_unpaid_order_can_be_cancelled_once()
    {
        var order = await NewOrderAsync();

        var cancelled = await _cashier.PostOk($"{Orders}/{order.Id()}/cancel", new { reason = "Customer left" });

        cancelled.GetProperty("status").GetString().Should().Be("Cancelled");
        cancelled.GetProperty("cancelledReason").GetString().Should().Be("Customer left");
        var ev = _f.Events.Published.OfType<OrderCancelledIntegrationEvent>().Single();
        (ev.OrderId, ev.TenantId, ev.WasPaid).Should().Be((order.Id(), _tenant, false));
        await _cashier.Post($"{Orders}/{order.Id()}/cancel", new { reason = "again" }).ShouldBe(409, "order.already_cancelled");
        await _f.As(AicosRoles.Cashier, _b2).Post($"{Orders}/{order.Id()}/cancel", new { }).ShouldBe(403, "branch.forbidden");
    }

    [Fact]
    public async Task A_paid_order_cannot_be_cancelled_at_the_counter_BR04()
    {
        var order = await NewOrderAsync();
        await _f.HandleAsync(Paid(order));

        await _cashier.Post($"{Orders}/{order.Id()}/cancel", new { reason = "x" }).ShouldBe(422, "order.paid");
        (await GetAsync(order.Id())).GetProperty("status").GetString().Should().Be("Confirmed");
    }

    // ------------------------------------------------------------------ what Payment tells us
    [Fact]
    public async Task A_payment_marks_the_order_paid_and_confirmed_and_is_idempotent()
    {
        var order = await NewOrderAsync();
        var paymentId = Guid.NewGuid();

        await _f.HandleAsync(Paid(order, "VietQR", paymentId));
        await _f.HandleAsync(Paid(order, "VietQR", paymentId));                                            // delivered twice

        var read = await GetAsync(order.Id());
        read.GetProperty("paymentStatus").GetString().Should().Be("Paid");
        read.GetProperty("status").GetString().Should().Be("Confirmed");
        read.GetProperty("paymentMethod").GetString().Should().Be("BankTransfer");
        read.GetProperty("paymentProvider").GetString().Should().Be("VietQR");
        read.GetProperty("paymentId").GetGuid().Should().Be(paymentId);
        var check = await _cashier.GetFromJsonAsync<JsonElement>($"{Orders}/{order.Id()}/payment");
        (check.GetProperty("isPaid").GetBoolean(), check.GetProperty("amountDue").GetDecimal()).Should().Be((true, 139_000m));
    }

    [Fact]
    public async Task A_payment_of_another_brand_or_for_an_unknown_order_changes_nothing()
    {
        var order = await NewOrderAsync();

        await _f.HandleAsync(Paid(order, tenant: _otherTenant));
        await _f.HandleAsync(new PaymentSucceededEvent(_tenant, _b1, Guid.NewGuid(), Guid.NewGuid(), 1, "Cash", DateTime.UtcNow));

        (await GetAsync(order.Id())).GetProperty("paymentStatus").GetString().Should().Be("Unpaid");
    }

    [Fact]
    public async Task Only_a_payment_that_ran_out_of_time_releases_the_order_BR25()
    {
        var order = await NewOrderAsync();
        var failed = new PaymentFailedEvent(_tenant, _b1, Guid.NewGuid(), order.Id(), "Momo", "Declined");

        await _f.HandleAsync(failed);                                                                      // a decline: the customer may try again
        (await GetAsync(order.Id())).GetProperty("status").GetString().Should().Be("Pending");

        await _f.HandleAsync(failed with { TimedOut = true });
        var read = await GetAsync(order.Id());
        read.GetProperty("status").GetString().Should().Be("Cancelled");
        read.GetProperty("cancelledReason").GetString().Should().Be("Not paid in time.");
        _f.Events.Published.OfType<OrderCancelledIntegrationEvent>().Should().ContainSingle();

        var paid = await NewOrderAsync();
        await _f.HandleAsync(Paid(paid));
        await _f.HandleAsync(new PaymentFailedEvent(_tenant, _b1, Guid.NewGuid(), paid.Id(), "Momo", "late", TimedOut: true));
        (await GetAsync(paid.Id())).GetProperty("status").GetString().Should().Be("Confirmed");          // a paid order is never released
    }

    [Fact]
    public async Task Money_that_arrives_for_a_cancelled_order_is_marked_as_needing_a_refund()
    {
        var order = await NewOrderAsync();
        await _cashier.PostOk($"{Orders}/{order.Id()}/cancel", new { reason = "x" });

        await _f.HandleAsync(Paid(order, "Momo"));

        var read = await GetAsync(order.Id());
        (read.GetProperty("status").GetString(), read.GetProperty("paymentStatus").GetString(), read.GetProperty("refundStatus").GetString()).Should().Be(("Cancelled", "Paid", "Needed"));
        (await _manager.GetFromJsonAsync<JsonElement>($"{Orders}/exceptions")).GetArrayLength().Should().Be(1);
    }

    // ------------------------------------------------------------------ voucher and points around the order (SCRUM-133)
    [Fact]
    public async Task The_voucher_is_redeemed_for_this_branch_and_this_member()
    {
        _vouchers.Percent["MEMBER10"] = 10;
        var member = Guid.NewGuid();

        var order = await _cashier.PostOk(Orders, new { type = "Takeaway", customerId = member, promotionCode = "MEMBER10", items = new[] { new { productId = _latte, quantity = 1 } } });

        _vouchers.Redeemed.Should().ContainSingle().Which.Should().Be(("MEMBER10", 55_000m, order.GetProperty("orderCode").GetString()!, _b1, (Guid?)member));
    }

    [Fact]
    public async Task A_payment_makes_one_paid_event_with_the_member_and_the_amount_to_pay()
    {
        _vouchers.Percent["SAVE10"] = 10;
        var member = Guid.NewGuid();
        var order = await _cashier.PostOk(Orders, Body(promo: "SAVE10", customerId: member));
        var paid = Paid(order, "Momo");

        await _f.HandleAsync(paid);
        await _f.HandleAsync(paid);                                                                 // delivered twice

        var ev = _f.Events.Published.OfType<OrderPaidIntegrationEvent>().Single();
        (ev.TenantId, ev.OrderId, ev.CustomerId, ev.Amount, ev.OrderCode).Should().Be((_tenant, order.Id(), (Guid?)member, 125_100m, order.GetProperty("orderCode").GetString()));
    }

    [Fact]
    public async Task Money_for_an_order_that_was_already_cancelled_earns_no_points()
    {
        var order = await NewOrderAsync(customerId: Guid.NewGuid());
        await _cashier.PostOk($"{Orders}/{order.Id()}/cancel", new { reason = "x" });

        await _f.HandleAsync(Paid(order, "Momo"));

        _f.Events.Published.OfType<OrderPaidIntegrationEvent>().Should().BeEmpty();
    }

    [Fact]
    public async Task Cancelling_or_timing_out_an_unpaid_order_tells_Customer_which_voucher_to_give_back()
    {
        _vouchers.Percent["A"] = 10;
        _vouchers.Percent["B"] = 10;
        var cancelled = await _cashier.PostOk(Orders, Body(promo: "A", customerId: Guid.NewGuid()));
        var timedOut = await _cashier.PostOk(Orders, Body(promo: "B"));

        await _cashier.PostOk($"{Orders}/{cancelled.Id()}/cancel", new { reason = "Customer left" });
        await _f.HandleAsync(new PaymentFailedEvent(_tenant, _b1, Guid.NewGuid(), timedOut.Id(), "Momo", "Not paid in time.", TimedOut: true));

        var events = _f.Events.Published.OfType<OrderCancelledIntegrationEvent>().ToList();
        events.Select(e => (e.PromotionCode, e.OrderCode, e.WasPaid)).Should().BeEquivalentTo(new[]
        {
            ("A", cancelled.GetProperty("orderCode").GetString(), false),
            ("B", timedOut.GetProperty("orderCode").GetString(), false),
        });
        events.First().CustomerId.Should().NotBeNull();
    }

    [Fact]
    public async Task A_paid_order_cancelled_as_an_exception_tells_Customer_to_take_the_points_back()
    {
        var member = Guid.NewGuid();
        var order = await NewOrderAsync(customerId: member);
        await _f.HandleAsync(Paid(order, "Cash"));

        await _manager.PostOk($"{Orders}/{order.Id()}/exception", new { reason = "Wrong drink" });

        var ev = _f.Events.Published.OfType<OrderCancelledIntegrationEvent>().Single();
        (ev.WasPaid, ev.CustomerId, ev.TotalAmount).Should().Be((true, (Guid?)member, 139_000m));
    }

    // ------------------------------------------------------------------ kitchen and hand-over (SCRUM-58, 61)
    [Fact]
    public async Task The_bar_moves_the_order_to_preparing_and_ready_and_the_cashier_hands_it_over()
    {
        var order = await NewOrderAsync();
        await _f.HandleAsync(Paid(order));
        await _f.HandleAsync(new KitchenOrderStartedIntegrationEvent(_tenant, Guid.NewGuid(), order.Id(), _b1, DateTime.UtcNow));
        (await GetAsync(order.Id())).GetProperty("status").GetString().Should().Be("Preparing");
        await _cashier.Post($"{Orders}/{order.Id()}/deliver", new { }).ShouldBe(422, "order.not_ready");

        await _f.HandleAsync(new KitchenOrderReadyIntegrationEvent(_tenant, Guid.NewGuid(), order.Id(), "X", _b1, null, DateTime.UtcNow));
        (await _cashier.GetFromJsonAsync<JsonElement>($"{Orders}?status=Ready")).GetArrayLength().Should().Be(1);        // what the cashier is told

        var delivered = await _cashier.PostOk($"{Orders}/{order.Id()}/deliver", new { });

        delivered.GetProperty("status").GetString().Should().Be("Completed");
        delivered.GetProperty("deliveredAtUtc").ValueKind.Should().Be(JsonValueKind.String);
        _f.Events.Published.OfType<OrderCompletedIntegrationEvent>().Should().ContainSingle().Which.TotalAmount.Should().Be(139_000);
        await _cashier.Post($"{Orders}/{order.Id()}/deliver", new { }).ShouldBe(409, "order.already_delivered");
    }

    [Fact]
    public async Task An_unpaid_dine_in_order_is_delivered_first_and_completes_when_it_is_paid()
    {
        var order = await NewOrderAsync(type: "DineIn");
        await _f.HandleAsync(new KitchenOrderReadyIntegrationEvent(_tenant, Guid.NewGuid(), order.Id(), "X", _b1, "T1", DateTime.UtcNow));

        var delivered = await _cashier.PostOk($"{Orders}/{order.Id()}/deliver", new { });
        delivered.GetProperty("status").GetString().Should().Be("Ready");                                  // not completed while unpaid
        _f.Events.Published.OfType<OrderCompletedIntegrationEvent>().Should().BeEmpty();

        await _f.HandleAsync(Paid(order));

        (await GetAsync(order.Id())).GetProperty("status").GetString().Should().Be("Completed");
        _f.Events.Published.OfType<OrderCompletedIntegrationEvent>().Should().ContainSingle();
    }

    [Fact]
    public async Task Only_counter_staff_hand_orders_over_and_a_cancelled_order_cannot_be_delivered()
    {
        var order = await NewOrderAsync();
        await _f.HandleAsync(new KitchenOrderReadyIntegrationEvent(_tenant, Guid.NewGuid(), order.Id(), "X", _b1, null, DateTime.UtcNow));

        await _f.As(AicosRoles.Barista, _b1).Post($"{Orders}/{order.Id()}/deliver", new { }).ShouldBe(403);
        await _f.As(AicosRoles.Cashier, _b2).Post($"{Orders}/{order.Id()}/deliver", new { }).ShouldBe(403, "branch.forbidden");
        await _cashier.PostOk($"{Orders}/{order.Id()}/cancel", new { reason = "x" });
        await _cashier.Post($"{Orders}/{order.Id()}/deliver", new { }).ShouldBe(422, "order.cancelled");
    }

    // ------------------------------------------------------------------ bill (SCRUM-60)
    [Fact]
    public async Task The_bill_counts_prints_and_shows_the_payment()
    {
        var order = await NewOrderAsync();
        await _f.HandleAsync(Paid(order, "Cash"));

        var first = await _cashier.GetFromJsonAsync<JsonElement>($"{Orders}/{order.Id()}/receipt");
        var again = await _cashier.GetFromJsonAsync<JsonElement>($"{Orders}/by-code/{order.GetProperty("orderCode").GetString()}/receipt");

        (first.GetProperty("isReprint").GetBoolean(), first.GetProperty("printedCount").GetInt32()).Should().Be((false, 1));
        (again.GetProperty("isReprint").GetBoolean(), again.GetProperty("printedCount").GetInt32()).Should().Be((true, 2));
        first.GetProperty("finalAmount").GetDecimal().Should().Be(139_000);
        first.GetProperty("paymentStatus").GetString().Should().Be("Paid");
        first.GetProperty("paymentMethod").GetString().Should().Be("Cash");
        first.GetProperty("items").GetArrayLength().Should().Be(2);
        await _customer.GetAsync($"{Orders}/{order.Id()}/receipt").ShouldBe(403);
        await _f.As(AicosRoles.Cashier, _b2).GetAsync($"{Orders}/{order.Id()}/receipt").ShouldBe(403, "branch.forbidden");
        await _cashier.GetAsync($"{Orders}/by-code/NOPE/receipt").ShouldBe(404);
    }

    [Fact]
    public async Task The_service_type_can_change_until_the_order_is_closed()
    {
        var order = await NewOrderAsync();

        var changed = await _cashier.Put($"{Orders}/{order.Id()}/service-type", new { type = "DineIn", tableNumber = "T5" });
        changed.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        (await changed.Body()).GetProperty("tableNumber").GetString().Should().Be("T5");

        await _cashier.Put($"{Orders}/{order.Id()}/service-type", new { type = "Delivery" }).ShouldBe(400);
        await _cashier.PostOk($"{Orders}/{order.Id()}/cancel", new { reason = "x" });
        await _cashier.Put($"{Orders}/{order.Id()}/service-type", new { type = "Takeaway" }).ShouldBe(422, "order.closed");
    }

    // ------------------------------------------------------------------ exceptions (SCRUM-49)
    [Fact]
    public async Task A_manager_cancels_a_paid_order_and_the_refund_is_tracked_until_it_is_paid_back()
    {
        var order = await NewOrderAsync();
        var paymentId = Guid.NewGuid();
        await _f.HandleAsync(Paid(order, "Momo", paymentId));

        var cancelled = await _manager.PostOk($"{Orders}/{order.Id()}/exception", new { reason = "Wrong drink made", refundKind = "Cancellation" });

        (cancelled.GetProperty("status").GetString(), cancelled.GetProperty("refundStatus").GetString()).Should().Be(("Cancelled", "RefundPending"));
        _payments.Refunds.Should().ContainSingle().Which.Should().Be((paymentId, "Cancellation", null, "Wrong drink made"));
        _f.Events.Published.OfType<OrderCancelledIntegrationEvent>().Single().WasPaid.Should().BeTrue();

        await _f.HandleAsync(new PaymentRefundedEvent(_tenant, _b1, paymentId, Guid.NewGuid(), order.Id(), 100_000, 100_000, "Momo"));
        var read = await GetAsync(order.Id());
        (read.GetProperty("refundStatus").GetString(), read.GetProperty("refundedAmount").GetDecimal(), read.GetProperty("paymentStatus").GetString()).Should().Be(("Refunded", 100_000m, "Paid"));

        await _f.HandleAsync(new PaymentRefundedEvent(_tenant, _b1, paymentId, Guid.NewGuid(), order.Id(), 39_000, 139_000, "Momo"));
        (await GetAsync(order.Id())).GetProperty("paymentStatus").GetString().Should().Be("Refunded");                  // all of it
    }


    [Fact]
    public async Task A_refund_that_is_paid_back_before_the_exception_is_saved_still_ends_as_refunded()
    {
        var order = await NewOrderAsync();
        var paymentId = Guid.NewGuid();
        await _f.HandleAsync(Paid(order, "Cash", paymentId));
        _payments.WhileOpen = () => _f.HandleAsync(new PaymentRefundedEvent(_tenant, _b1, paymentId, Guid.NewGuid(), order.Id(), 139_000, 139_000, "Cash"));

        var cancelled = await _manager.PostOk($"{Orders}/{order.Id()}/exception", new { reason = "Wrong order" });

        (cancelled.GetProperty("status").GetString(), cancelled.GetProperty("refundStatus").GetString(), cancelled.GetProperty("paymentStatus").GetString()).Should().Be(("Cancelled", "Refunded", "Refunded"));
        (await GetAsync(order.Id())).GetProperty("refundStatus").GetString().Should().Be("Refunded");
    }
    [Fact]
    public async Task A_refund_that_fails_or_is_rejected_shows_on_the_order()
    {
        var order = await NewOrderAsync();
        var paymentId = Guid.NewGuid();
        await _f.HandleAsync(Paid(order, "Momo", paymentId));
        await _manager.PostOk($"{Orders}/{order.Id()}/exception", new { reason = "Complaint", refundKind = "Exception", amount = 20_000 });
        _payments.Refunds.Single().Amount.Should().Be(20_000);

        await _f.HandleAsync(new PaymentRefundFailedEvent(_tenant, _b1, paymentId, Guid.NewGuid(), order.Id(), "Rejected"));

        (await GetAsync(order.Id())).GetProperty("refundStatus").GetString().Should().Be("RefundFailed");
        (await _manager.GetFromJsonAsync<JsonElement>($"{Orders}/exceptions?refundStatus=RefundFailed")).GetArrayLength().Should().Be(1);
        (await _manager.GetFromJsonAsync<JsonElement>($"{Orders}/exceptions?refundStatus=Refunded")).GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task The_exception_path_is_for_managers_and_for_paid_orders_only()
    {
        var unpaid = await NewOrderAsync();
        var paid = await NewOrderAsync();
        await _f.HandleAsync(Paid(paid, "Cash"));

        await _manager.Post($"{Orders}/{unpaid.Id()}/exception", new { reason = "x" }).ShouldBe(422, "order.not_paid");
        await _cashier.Post($"{Orders}/{paid.Id()}/exception", new { reason = "x" }).ShouldBe(403);
        await _manager.Post($"{Orders}/{paid.Id()}/exception", new { }).ShouldBe(400);
        await _manager.Post($"{Orders}/{paid.Id()}/exception", new { reason = "x", refundKind = "Whatever" }).ShouldBe(400);
        await _manager.Post($"{Orders}/{paid.Id()}/exception", new { reason = "x", refundKind = "Exception" }).ShouldBe(400);     // an exception needs an amount
        await _f.As(AicosRoles.BranchManager, _b2).Post($"{Orders}/{paid.Id()}/exception", new { reason = "x" }).ShouldBe(403, "branch.forbidden");
        await _otherAdmin.Post($"{Orders}/{paid.Id()}/exception", new { reason = "x" }).ShouldBe(404);
        await _manager.PostOk($"{Orders}/{paid.Id()}/exception", new { reason = "x" });
        await _manager.Post($"{Orders}/{paid.Id()}/exception", new { reason = "x" }).ShouldBe(409, "order.already_cancelled");
    }

    [Fact]
    public async Task When_payment_refuses_the_request_the_order_is_left_as_it_was()
    {
        var order = await NewOrderAsync();
        await _f.HandleAsync(Paid(order, "Cash"));
        _payments.Fail = new BusinessRuleException("refund.nothing_to_refund", "The brand policy refunds nothing for this case.");

        await _manager.Post($"{Orders}/{order.Id()}/exception", new { reason = "x", refundKind = "NoShow" }).ShouldBe(422, "refund.nothing_to_refund");

        var read = await GetAsync(order.Id());
        (read.GetProperty("status").GetString(), read.GetProperty("refundStatus").GetString()).Should().Be(("Confirmed", "None"));
        _f.Events.Published.OfType<OrderCancelledIntegrationEvent>().Should().BeEmpty();
    }
}
