using System.Net.Http.Json;
using System.Text.Json;
using AICOS.BuildingBlocks.Security;
using AICOS.Contracts.Events;
using AICOS.Ordering.Api;
using AICOS.Ordering.Api.Features.Qr;
using AICOS.Ordering.Api.Persistence;
using AICOS.Ordering.Api.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AICOS.Services.IntegrationTests;

/// <summary>Stands in for the Table service: which table a scanned QR code belongs to.</summary>
public sealed class FakeTableQr : ITableQrClient
{
    private readonly Dictionary<string, TableQrInfo> _codes = [];

    public string Add(Guid tenantId, Guid branchId, string tableCode, bool canOrder = true)
    {
        var token = Guid.NewGuid().ToString("N")[..16];
        _codes[token] = new TableQrInfo(tenantId, branchId, Guid.NewGuid(), tableCode, "Tầng trệt", canOrder);
        return token;
    }

    public Task<TableQrInfo?> LookupAsync(string qrToken, CancellationToken ct) => Task.FromResult(_codes.GetValueOrDefault(qrToken.Trim()));
}

/// <summary>Ordering from the table QR code without an account: one session and one shared cart per table, several rounds, one bill (FR-14, UC-49).</summary>
public sealed class QrOrderTests : IDisposable
{
    private const string Qr = "/api/v1/orders/qr";
    private const string Header = "X-Table-Session";
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 3, 0, 0, TimeSpan.Zero);

    private readonly FakeMenu _menu = new();
    private readonly FakeTableQr _tables = new();
    private readonly FakeVouchers _vouchers = new();
    private readonly ServiceFactory<OrderingService, OrderingDbContext> _f;
    private readonly Guid _tenant = ServiceFactory<OrderingService, OrderingDbContext>.DefaultTenantId;
    private readonly Guid _otherTenant = Guid.NewGuid();
    private readonly Guid _b1;
    private readonly Guid _b2;
    private readonly Guid _latte;
    private readonly Guid _cake;
    private readonly Guid _sizeL = Guid.NewGuid();
    private readonly Guid _sizeM = Guid.NewGuid();
    private readonly string _t1;
    private readonly string _t2;
    private readonly HttpClient _guest;

    public QrOrderTests()
    {
        _f = new ServiceFactory<OrderingService, OrderingDbContext>(services =>
        {
            services.RemoveAll<IMenuCatalogClient>();
            services.AddSingleton<IMenuCatalogClient>(_menu);
            services.RemoveAll<ITableQrClient>();
            services.AddSingleton<ITableQrClient>(_tables);
            services.RemoveAll<IVoucherClient>();
            services.AddSingleton<IVoucherClient>(_vouchers);
        });
        _f.Clock.Now = Now;
        _b1 = _f.Branches.Add(_tenant, name: "Quận 1");
        _b2 = _f.Branches.Add(_tenant, name: "Quận 3");
        _latte = _menu.Add(_b1, "Latte", 55_000);
        _cake = _menu.Add(_b1, "Bánh", 30_000);
        _menu.SetOptions(_b1, _latte, new MenuOptionGroupInfo(Guid.NewGuid(), "Size", false, true, null, [new(_sizeM, "M", 0, true, null), new(_sizeL, "L", 5_000, false, null)]));
        _t1 = _tables.Add(_tenant, _b1, "A1");
        _t2 = _tables.Add(_tenant, _b1, "A2");
        _guest = _f.CreateClient();
    }

    public void Dispose() => _f.Dispose();

    private async Task<(Guid SessionId, HttpClient Phone, Guid OrderId)> TableWithAnOrderAsync(string? code = null)
    {
        var (token, sessionId) = await ScanAsync(code);
        var phone = _f.CreateClient();
        phone.DefaultRequestHeaders.Add(Header, token);
        (await phone.PostAsJsonAsync($"{Qr}/cart/items", new { productId = _cake, quantity = 1 })).EnsureSuccessStatusCode();
        var order = await (await phone.PostAsJsonAsync($"{Qr}/checkout", new { note = (string?)null })).Content.ReadFromJsonAsync<JsonElement>();
        return (sessionId, phone, order.GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task A_table_asks_for_the_bill_and_the_counter_sees_it()
    {
        var (sessionId, phone, _) = await TableWithAnOrderAsync();
        (await phone.PostAsJsonAsync($"{Qr}/session/bill-request", new { method = "bitcoin" })).StatusCode.Should().Be(System.Net.HttpStatusCode.BadRequest);

        (await phone.PostAsJsonAsync($"{Qr}/session/bill-request", new { method = "transfer" })).StatusCode.Should().Be(System.Net.HttpStatusCode.NoContent);

        _f.Events.Published.OfType<TableBillRequestedIntegrationEvent>().Should().ContainSingle(e => e.SessionId == sessionId && e.Method == "Transfer" && e.TableCode == "A1");
        using var cashier = _f.As(AicosRoles.Cashier, _b1);
        var sessions = await cashier.GetFromJsonAsync<JsonElement>($"{Qr}/sessions");
        var row = sessions.EnumerateArray().Single(s => s.GetProperty("id").GetGuid() == sessionId);
        row.GetProperty("billRequestedMethod").GetString().Should().Be("Transfer");
        row.GetProperty("billRequestedAtUtc").GetDateTime().Should().NotBe(default);
    }

    [Fact]
    public async Task A_table_with_nothing_to_pay_cannot_ask_for_the_bill()
    {
        var (token, _) = await ScanAsync();
        await Phone(token).PostAsJsonAsync($"{Qr}/session/bill-request", new { method = "Cash" }).ShouldBe(422, "qr.nothing_to_pay");
    }

    [Fact]
    public async Task The_payment_token_of_a_table_reads_only_the_orders_of_that_table()
    {
        var (sessionId, phone, orderId) = await TableWithAnOrderAsync();
        var (_, _, otherOrderId) = await TableWithAnOrderAsync(_t2);

        _f.Clock.Now = DateTimeOffset.UtcNow; // the token is signed with the test clock but validated with the real one
        var access = await (await phone.PostAsync($"{Qr}/session/payment-access", null)).Content.ReadFromJsonAsync<JsonElement>();
        access.GetProperty("branchId").GetGuid().Should().Be(_b1);

        using var table = _f.CreateClient();
        table.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", access.GetProperty("accessToken").GetString());
        (await table.GetAsync($"/api/v1/orders/{orderId}")).StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        (await table.GetAsync($"/api/v1/orders/{otherOrderId}")).StatusCode.Should().Be(System.Net.HttpStatusCode.NotFound);
        (await table.GetAsync("/api/v1/orders")).StatusCode.Should().Be(System.Net.HttpStatusCode.Forbidden);
        (await table.GetAsync($"{Qr}/sessions")).StatusCode.Should().Be(System.Net.HttpStatusCode.Forbidden);
        sessionId.Should().NotBeEmpty();
    }

    [Fact]
    public async Task No_payment_token_without_a_table_session()
    {
        await _guest.PostAsync($"{Qr}/session/payment-access", null).ShouldBe(401, "qr.session_invalid");
    }

    private async Task<(string Token, Guid SessionId)> ScanAsync(string? code = null, HttpClient? client = null)
    {
        var scan = await (await (client ?? _guest).Post($"{Qr}/scan", new { qrToken = code ?? _t1 })).Body();
        return (scan.GetProperty("sessionToken").GetString()!, scan.GetProperty("sessionId").GetGuid());
    }

    private HttpClient Phone(string token)
    {
        var client = _f.CreateClient();
        client.DefaultRequestHeaders.Add(Header, token);
        return client;
    }

    private async Task<JsonElement> AddAsync(HttpClient phone, Guid? product = null, int quantity = 1, string? note = null, Guid[]? options = null) =>
        await phone.PostOk($"{Qr}/cart/items", new { productId = product ?? _latte, quantity, note, optionIds = options });

    private PaymentSucceededEvent Paid(JsonElement order, Guid? tenant = null) =>
        new(tenant ?? _tenant, _b1, Guid.NewGuid(), order.Id(), order.GetProperty("finalAmount").GetDecimal(), "Cash", Now.UtcDateTime);

    // ------------------------------------------------------------------ scanning
    [Fact]
    public async Task Scanning_a_table_code_opens_a_session_and_everyone_at_the_table_shares_it()
    {
        var first = await (await _guest.Post($"{Qr}/scan", new { qrToken = _t1 })).Body();
        var second = await (await _f.CreateClient().Post($"{Qr}/scan", new { qrToken = _t1 })).Body();
        var other = await (await _guest.Post($"{Qr}/scan", new { qrToken = _t2 })).Body();

        first.GetProperty("tableCode").GetString().Should().Be("A1");
        (first.GetProperty("tenantId").GetGuid(), first.GetProperty("branchId").GetGuid()).Should().Be((_tenant, _b1));
        second.GetProperty("sessionId").GetGuid().Should().Be(first.GetProperty("sessionId").GetGuid());       // a second phone joins the same sitting
        second.GetProperty("sessionToken").GetString().Should().Be(first.GetProperty("sessionToken").GetString());
        other.GetProperty("sessionId").GetGuid().Should().NotBe(first.GetProperty("sessionId").GetGuid());
    }

    [Fact]
    public async Task An_unknown_empty_or_closed_table_code_is_refused()
    {
        await _guest.Post($"{Qr}/scan", new { qrToken = "nope" }).ShouldBe(404);
        await _guest.Post($"{Qr}/scan", new { qrToken = " " }).ShouldBe(400);
        await _guest.Post($"{Qr}/scan", new { qrToken = _tables.Add(_tenant, _b1, "A9", canOrder: false) }).ShouldBe(422, "qr.ordering_closed");
    }

    [Fact]
    public async Task Without_a_valid_session_token_nothing_can_be_done()
    {
        var (token, _) = await ScanAsync();
        var forged = token[..^3] + (token.EndsWith("abc") ? "xyz" : "abc");

        await _f.CreateClient().GetAsync($"{Qr}/cart").ShouldBe(401, "qr.session_invalid");
        await Phone(forged).GetAsync($"{Qr}/cart").ShouldBe(401, "qr.session_invalid");
        await Phone($"{Guid.NewGuid():N}.AAAA").GetAsync($"{Qr}/session/bill").ShouldBe(401, "qr.session_invalid");
        await _f.CreateClient().Post($"{Qr}/checkout", new { }).ShouldBe(401, "qr.session_invalid");
    }

    // ------------------------------------------------------------------ the shared cart
    [Fact]
    public async Task The_cart_is_priced_from_the_menu_and_shared_by_the_phones_of_the_table()
    {
        var (token, sessionId) = await ScanAsync();
        var phone1 = Phone(token);
        var phone2 = Phone(token);

        await AddAsync(phone1, quantity: 2, note: "ít đá", options: [_sizeL]);
        var cart = await AddAsync(phone2, _cake, 1);

        cart.GetProperty("sessionId").GetGuid().Should().Be(sessionId);
        cart.GetProperty("lines").GetArrayLength().Should().Be(2);
        cart.GetProperty("itemCount").GetInt32().Should().Be(3);
        cart.GetProperty("total").GetDecimal().Should().Be(2 * 60_000 + 30_000);                // L adds 5,000
        var latte = cart.GetProperty("lines").EnumerateArray().Single(l => l.GetProperty("productName").GetString() == "Latte");
        (latte.GetProperty("unitPrice").GetDecimal(), latte.GetProperty("note").GetString()).Should().Be((60_000m, "ít đá"));
        (await phone1.GetFromJsonAsync<JsonElement>($"{Qr}/cart")).GetProperty("lines").GetArrayLength().Should().Be(2);   // the first phone sees the second one's item
    }

    [Fact]
    public async Task The_same_item_is_one_line_and_a_different_choice_is_another_line()
    {
        var phone = Phone((await ScanAsync()).Token);

        await AddAsync(phone, quantity: 1, options: [_sizeL]);
        await AddAsync(phone, quantity: 2, options: [_sizeL]);
        var cart = await AddAsync(phone, quantity: 1, options: [_sizeM]);

        var lines = cart.GetProperty("lines").EnumerateArray().ToList();
        lines.Should().HaveCount(2);
        lines.Single(l => l.GetProperty("unitPrice").GetDecimal() == 60_000).GetProperty("quantity").GetInt32().Should().Be(3);
    }

    [Fact]
    public async Task Quantities_can_be_changed_and_lines_removed()
    {
        var phone = Phone((await ScanAsync()).Token);
        var line = (await AddAsync(phone, quantity: 1)).GetProperty("lines")[0].GetProperty("lineId").GetGuid();
        await AddAsync(phone, _cake, 1);

        var changed = await (await phone.Put($"{Qr}/cart/items/{line}", new { quantity = 5 })).Body();
        changed.GetProperty("itemCount").GetInt32().Should().Be(6);

        var removed = await (await phone.Put($"{Qr}/cart/items/{line}", new { quantity = 0 })).Body();
        removed.GetProperty("lines").GetArrayLength().Should().Be(1);
        await phone.Put($"{Qr}/cart/items/{line}", new { quantity = 2 }).ShouldBe(404);
        await phone.Put($"{Qr}/cart/items/{Guid.NewGuid()}", new { quantity = 100 }).ShouldBe(400);

        await phone.DeleteAsync($"{Qr}/cart").ShouldBe(204);
        (await phone.GetFromJsonAsync<JsonElement>($"{Qr}/cart")).GetProperty("lines").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task An_item_that_is_not_sold_or_a_bad_choice_is_refused_when_it_is_added()
    {
        var phone = Phone((await ScanAsync()).Token);

        await phone.Post($"{Qr}/cart/items", new { productId = Guid.NewGuid(), quantity = 1 }).ShouldBe(422, "qr.item_unavailable");
        await phone.Post($"{Qr}/cart/items", new { productId = _latte, quantity = 1, optionIds = new[] { Guid.NewGuid() } }).ShouldBe(422, "order.option_invalid");
        await phone.Post($"{Qr}/cart/items", new { productId = _latte, quantity = 0 }).ShouldBe(400);
        await phone.Post($"{Qr}/cart/items", new { quantity = 1 }).ShouldBe(400);
        await phone.Post($"{Qr}/cart/items", new { productId = _latte, quantity = 1, note = new string('x', 301) }).ShouldBe(400);
    }

    [Fact]
    public async Task A_price_sent_by_the_phone_is_ignored()
    {
        var phone = Phone((await ScanAsync()).Token);

        var cart = await phone.PostOk($"{Qr}/cart/items", new { productId = _latte, quantity = 1, unitPrice = 1, productName = "Hacked" });

        cart.GetProperty("lines")[0].GetProperty("unitPrice").GetDecimal().Should().Be(55_000);
        cart.GetProperty("lines")[0].GetProperty("productName").GetString().Should().Be("Latte");
    }

    [Fact]
    public async Task A_cart_item_that_sold_out_in_the_meantime_is_flagged_and_left_out_of_the_total()
    {
        var phone = Phone((await ScanAsync()).Token);
        await AddAsync(phone, _latte, 1);
        await AddAsync(phone, _cake, 2);
        _menu.SoldOut(_b1, _cake);

        var cart = await phone.GetFromJsonAsync<JsonElement>($"{Qr}/cart");

        cart.GetProperty("hasProblems").GetBoolean().Should().BeTrue();
        cart.GetProperty("total").GetDecimal().Should().Be(55_000);
        cart.GetProperty("lines").EnumerateArray().Single(l => l.GetProperty("problem").ValueKind == JsonValueKind.String).GetProperty("problem").GetString().Should().Be("qr.item_unavailable");
    }

    [Fact]
    public async Task When_the_cart_store_is_down_the_phone_is_told_to_try_again_and_nothing_is_kept_in_memory()
    {
        var phone = Phone((await ScanAsync()).Token);
        _f.Hashes.Down = true;

        await phone.GetAsync($"{Qr}/cart").ShouldBe(503, "dependency_unavailable");
        await phone.Post($"{Qr}/cart/items", new { productId = _latte, quantity = 1 }).ShouldBe(503, "dependency_unavailable");

        _f.Hashes.Down = false;
        (await phone.GetFromJsonAsync<JsonElement>($"{Qr}/cart")).GetProperty("lines").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Carts_of_different_tables_and_brands_are_kept_apart_by_key()
    {
        var a = Phone((await ScanAsync(_t1)).Token);
        var b = Phone((await ScanAsync(_t2)).Token);
        await AddAsync(a, quantity: 1);

        (await b.GetFromJsonAsync<JsonElement>($"{Qr}/cart")).GetProperty("lines").GetArrayLength().Should().Be(0);
        _f.Hashes.Keys.Should().OnlyContain(k => k.StartsWith($"t:{_tenant:N}:qrcart:"));
    }

    // ------------------------------------------------------------------ ordering rounds
    [Fact]
    public async Task Checkout_turns_the_cart_into_an_order_for_the_table_and_the_bar_is_told()
    {
        var (token, sessionId) = await ScanAsync();
        var phone = Phone(token);
        await AddAsync(phone, quantity: 2, note: "ít đá", options: [_sizeL]);

        var response = await phone.Post($"{Qr}/checkout", new { note = "Mang ra bàn A1" });

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.Created);
        var order = await response.Body();
        (order.GetProperty("type").GetString(), order.GetProperty("tableNumber").GetString(), order.GetProperty("status").GetString()).Should().Be(("DineIn", "A1", "Pending"));
        order.GetProperty("tableSessionId").GetGuid().Should().Be(sessionId);
        order.GetProperty("finalAmount").GetDecimal().Should().Be(120_000);
        order.GetProperty("customerId").ValueKind.Should().Be(JsonValueKind.Null);                     // a guest
        var created = _f.Events.Published.OfType<OrderCreatedIntegrationEvent>().Single();
        (created.OrderType, created.TableNumber, created.IsPaid, created.TenantId).Should().Be(("DineIn", "A1", false, _tenant));
        created.Items.Single().Note.Should().Contain("L").And.EndWith("ít đá");
        (await phone.GetFromJsonAsync<JsonElement>($"{Qr}/cart")).GetProperty("lines").GetArrayLength().Should().Be(0);     // the cart was emptied
        await phone.Post($"{Qr}/checkout", new { }).ShouldBe(422, "qr.cart_empty");
    }

    [Fact]
    public async Task A_cart_with_an_item_that_cannot_be_ordered_creates_nothing_and_stays_in_the_cart()
    {
        var phone = Phone((await ScanAsync()).Token);
        await AddAsync(phone, _latte, 1);
        await AddAsync(phone, _cake, 1);
        _menu.SoldOut(_b1, _cake);

        await phone.Post($"{Qr}/checkout", new { }).ShouldBe(422, "qr.cart_has_problems");

        _f.Events.Published.OfType<OrderCreatedIntegrationEvent>().Should().BeEmpty();
        (await phone.GetFromJsonAsync<JsonElement>($"{Qr}/cart")).GetProperty("lines").GetArrayLength().Should().Be(2);
    }

    [Fact]
    public async Task Two_phones_pressing_order_at_once_make_one_order()
    {
        var token = (await ScanAsync()).Token;
        await AddAsync(Phone(token), quantity: 1);
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Phone(token).Post($"{Qr}/checkout", new { })));

        results.Count(r => r.StatusCode == System.Net.HttpStatusCode.Created).Should().Be(1);
        results.Count(r => (int)r.StatusCode == 422).Should().Be(3);
        _f.Events.Published.OfType<OrderCreatedIntegrationEvent>().Should().ContainSingle();
    }

    [Fact]
    public async Task Further_rounds_are_further_orders_and_the_bill_adds_them_up()
    {
        var phone = Phone((await ScanAsync()).Token);
        await AddAsync(phone, quantity: 2);
        var first = await (await phone.Post($"{Qr}/checkout", new { })).Body();
        await AddAsync(phone, _cake, 1);
        var second = await (await phone.Post($"{Qr}/checkout", new { })).Body();
        await _f.HandleAsync(Paid(first));

        var orders = await phone.GetFromJsonAsync<JsonElement>($"{Qr}/orders");
        var bill = await phone.GetFromJsonAsync<JsonElement>($"{Qr}/session/bill");

        orders.GetArrayLength().Should().Be(2);
        orders.EnumerateArray().Select(o => o.GetProperty("paymentStatus").GetString()).Should().BeEquivalentTo("Paid", "Unpaid");
        (bill.GetProperty("total").GetDecimal(), bill.GetProperty("paid").GetDecimal(), bill.GetProperty("due").GetDecimal()).Should().Be((140_000m, 110_000m, 30_000m));
        bill.GetProperty("orders").GetArrayLength().Should().Be(2);
        bill.GetProperty("orders")[0].GetProperty("items")[0].GetProperty("productName").GetString().Should().Be("Latte");
        second.Id().Should().NotBe(first.Id());
    }

    [Fact]
    public async Task A_member_who_signs_in_earns_the_order_and_a_guest_does_not()
    {
        var phone = Phone((await ScanAsync()).Token);
        var memberId = Guid.NewGuid();
        var member = _f.As(AicosRoles.Customer, userId: memberId);
        member.DefaultRequestHeaders.Add(Header, phone.DefaultRequestHeaders.GetValues(Header).Single());
        var stranger = _f.As(AicosRoles.Customer, userId: Guid.NewGuid(), tenantId: _otherTenant);
        stranger.DefaultRequestHeaders.Add(Header, phone.DefaultRequestHeaders.GetValues(Header).Single());

        await AddAsync(member, quantity: 1);
        var mine = await (await member.Post($"{Qr}/checkout", new { })).Body();
        await AddAsync(stranger, _cake, 1);
        var other = await (await stranger.Post($"{Qr}/checkout", new { })).Body();

        mine.GetProperty("customerId").GetGuid().Should().Be(memberId);
        other.GetProperty("customerId").ValueKind.Should().Be(JsonValueKind.Null);              // a member of another brand earns nothing here
    }

    [Fact]
    public async Task An_order_a_guest_places_from_the_table_is_paid_before_the_bar_makes_it()
    {
        var phone = Phone((await ScanAsync()).Token);
        await AddAsync(phone, _cake, 1);

        await phone.Post($"{Qr}/checkout", new { }).ShouldBe(201);

        _f.Events.Published.OfType<OrderCreatedIntegrationEvent>().Single().PayFirst.Should().BeTrue();
    }

    [Fact]
    public async Task A_member_uses_a_voucher_at_the_table_and_a_guest_is_asked_to_sign_in()
    {
        _vouchers.Percent["TABLE10"] = 10;
        var phone = Phone((await ScanAsync()).Token);
        await AddAsync(phone, _cake, 1);

        await phone.Post($"{Qr}/checkout", new { promotionCode = "TABLE10" }).ShouldBe(422, "voucher.sign_in_required");
        _vouchers.Redeemed.Should().BeEmpty();                                                       // the cart is kept for the next try

        var memberId = Guid.NewGuid();
        var member = _f.As(AicosRoles.Customer, userId: memberId);
        member.DefaultRequestHeaders.Add(Header, phone.DefaultRequestHeaders.GetValues(Header).Single());
        var order = await (await member.Post($"{Qr}/checkout", new { promotionCode = "TABLE10" })).Body();

        (order.GetProperty("discountAmount").GetDecimal(), order.GetProperty("finalAmount").GetDecimal()).Should().Be((3_000m, 27_000m));
        _vouchers.Redeemed.Single().Customer.Should().Be(memberId);
    }

    // ------------------------------------------------------------------ the sitting ends
    [Fact]
    public async Task When_every_round_is_paid_the_session_closes_and_a_new_scan_opens_a_new_one()
    {
        var (token, sessionId) = await ScanAsync();
        var phone = Phone(token);
        await AddAsync(phone, quantity: 1);
        var first = await (await phone.Post($"{Qr}/checkout", new { })).Body();
        await AddAsync(phone, _cake, 1);
        var second = await (await phone.Post($"{Qr}/checkout", new { })).Body();

        await _f.HandleAsync(Paid(first));
        await AddAsync(phone, _cake, 1);                                                          // one round is still unpaid: the table can keep ordering
        await _f.HandleAsync(Paid(second));

        await phone.Post($"{Qr}/cart/items", new { productId = _cake, quantity = 1 }).ShouldBe(409, "qr.session_closed");
        await phone.Post($"{Qr}/checkout", new { }).ShouldBe(409, "qr.session_closed");
        (await phone.GetFromJsonAsync<JsonElement>($"{Qr}/session/bill")).GetProperty("due").GetDecimal().Should().Be(0);   // the bill stays readable
        (await phone.GetFromJsonAsync<JsonElement>($"{Qr}/orders")).GetArrayLength().Should().Be(2);

        (await ScanAsync()).SessionId.Should().NotBe(sessionId);
    }

    [Fact]
    public async Task A_cancelled_round_does_not_keep_the_session_open()
    {
        var (token, sessionId) = await ScanAsync();
        var phone = Phone(token);
        await AddAsync(phone, quantity: 1);
        var first = await (await phone.Post($"{Qr}/checkout", new { })).Body();
        await AddAsync(phone, _cake, 1);
        var second = await (await phone.Post($"{Qr}/checkout", new { })).Body();
        await _f.HandleAsync(Paid(first));

        await _f.As(AicosRoles.Cashier, _b1).Post($"/api/v1/orders/{second.Id()}/cancel", new { reason = "Khách đổi ý" }).ShouldBe(200);

        await phone.Post($"{Qr}/cart/items", new { productId = _cake, quantity = 1 }).ShouldBe(409, "qr.session_closed");
        var bill = await phone.GetFromJsonAsync<JsonElement>($"{Qr}/session/bill");
        (bill.GetProperty("status").GetString(), bill.GetProperty("total").GetDecimal()).Should().Be(("Closed", 55_000m));   // the cancelled round is not counted
        sessionId.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Staff_list_the_open_tables_with_what_is_due_and_close_a_table_by_hand()
    {
        var (token, sessionId) = await ScanAsync();
        var phone = Phone(token);
        await AddAsync(phone, quantity: 1);
        await phone.Post($"{Qr}/checkout", new { }).ShouldBe(201);
        var cashier = _f.As(AicosRoles.Cashier, _b1);

        var list = await cashier.GetFromJsonAsync<JsonElement>($"{Qr}/sessions");
        var row = list.EnumerateArray().Single();
        (row.GetProperty("tableCode").GetString(), row.GetProperty("orders").GetInt32(), row.GetProperty("due").GetDecimal()).Should().Be(("A1", 1, 55_000m));
        (await cashier.GetFromJsonAsync<JsonElement>($"{Qr}/sessions/{sessionId}/bill")).GetProperty("due").GetDecimal().Should().Be(55_000);

        await _f.As(AicosRoles.Cashier, _b2).Post($"{Qr}/sessions/{sessionId}/close", new { }).ShouldBe(403, "branch.forbidden");
        await _f.As(AicosRoles.Cashier, _b1, tenantId: _otherTenant).Post($"{Qr}/sessions/{sessionId}/close", new { }).ShouldBe(404);
        await phone.Post($"{Qr}/sessions/{sessionId}/close", new { }).ShouldBe(401);                              // a guest cannot close it

        await cashier.Post($"{Qr}/sessions/{sessionId}/close", new { }).ShouldBe(409, "qr.session_unsettled");
        var unpaidBill = await cashier.GetFromJsonAsync<JsonElement>($"{Qr}/sessions/{sessionId}/bill");
        var unpaidOrderId = unpaidBill.GetProperty("orders")[0].GetProperty("orderId").GetGuid();
        await cashier.Post($"/api/v1/orders/{unpaidOrderId}/cancel", new { reason = "Test settlement guard; guest changed mind" }).ShouldBe(200);
        await cashier.Post($"{Qr}/sessions/{sessionId}/close", new { }).ShouldBe(204);
        await phone.Post($"{Qr}/cart/items", new { productId = _latte, quantity = 1 }).ShouldBe(409, "qr.session_closed");
        (await cashier.GetFromJsonAsync<JsonElement>($"{Qr}/sessions")).GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task A_session_nobody_used_for_six_hours_is_closed_by_the_timer()
    {
        var (activeToken, _) = await ScanAsync(_t1);
        await ScanAsync(_t2);
        _f.Clock.Now = Now.AddHours(5);
        await AddAsync(Phone(activeToken), quantity: 1);                                         // the first table is used again after five hours
        _f.Clock.Now = Now.AddHours(7);

        using var scope = _f.Services.CreateScope();
        var closed = await TableSessionExpiryWorker.CloseIdleAsync(scope.ServiceProvider.GetRequiredService<OrderingDbContext>(), _f.Clock.Now.UtcDateTime, TimeSpan.FromHours(6), default);

        closed.Should().Be(1);                                                                    // only the table that stayed idle
        var cashier = _f.As(AicosRoles.Cashier, _b1);
        (await cashier.GetFromJsonAsync<JsonElement>($"{Qr}/sessions")).EnumerateArray().Single().GetProperty("tableCode").GetString().Should().Be("A1");
    }
}
