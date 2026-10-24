using System.Net.Http.Json;
using System.Text.Json;
using AICOS.BuildingBlocks.Security;
using AICOS.Contracts.Events;
using AICOS.CustomerEngagement.Api;
using AICOS.CustomerEngagement.Api.Persistence;

namespace AICOS.Services.IntegrationTests;

/// <summary>SCRUM-133 (voucher and points around an order) and SCRUM-134 (points turned into a voucher).</summary>
public sealed class LoyaltyRedemptionTests : IDisposable
{
    private const string Customers = "/api/v1/customers";
    private const string Vouchers = "/api/v1/customers/vouchers";
    private const string Rules = "/api/v1/customers/loyalty/rules";

    private readonly ServiceFactory<CustomerEngagementService, CustomerEngagementDbContext> _f = new();
    private readonly Guid _tenant = ServiceFactory<CustomerEngagementService, CustomerEngagementDbContext>.DefaultTenantId;
    private readonly Guid _otherTenant = Guid.NewGuid();
    private readonly Guid _b1;
    private readonly Guid _b2;
    private readonly HttpClient _admin;
    private readonly HttpClient _otherAdmin;
    private readonly HttpClient _cashier;

    public LoyaltyRedemptionTests()
    {
        _b1 = _f.Branches.Add(_tenant, name: "Quận 1");
        _b2 = _f.Branches.Add(_tenant, name: "Quận 3");
        _admin = _f.As(AicosRoles.BrandAdmin);
        _otherAdmin = _f.As(AicosRoles.BrandAdmin, tenantId: _otherTenant);
        _cashier = _f.As(AicosRoles.Cashier, _b1);
        // 1 point per 1,000 VND spent, 1 point worth 1,000 VND, at least 10 points to redeem
        _admin.Put(Rules, new { isActive = true, amountPerPoint = 1_000, redeemValuePerPoint = 1_000, minPointsToRedeem = 10, silverThreshold = 100, goldThreshold = 500 }).ShouldBe(200).GetAwaiter().GetResult();
    }

    public void Dispose() => _f.Dispose();

    private async Task<(Guid UserId, Guid CustomerId, HttpClient Client)> MemberAsync(string name, Guid? tenant = null)
    {
        var userId = Guid.NewGuid();
        await _f.HandleAsync(new CustomerRegisteredEvent(userId, name, $"{userId:N}@example.com", null, tenant ?? _tenant));
        var client = _f.As(AicosRoles.Customer, userId: userId, tenantId: tenant);
        return (userId, (await client.GetFromJsonAsync<JsonElement>($"{Customers}/me")).Id(), client);
    }

    private OrderPaidIntegrationEvent Paid(Guid customer, decimal amount, Guid? order = null, Guid? tenant = null) =>
        new(tenant ?? _tenant, order ?? Guid.NewGuid(), "ORD-1", _b1, customer, amount, DateTime.UtcNow);

    private static async Task<int> BalanceAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<JsonElement>($"{Customers}/me/loyalty")).GetProperty("pointsBalance").GetInt32();

    // ------------------------------------------------------------------ points after payment (SCRUM-133)
    [Fact]
    public async Task Points_are_awarded_when_the_order_is_paid_once_and_by_profile_or_account_id()
    {
        var (userId, customerId, me) = await MemberAsync("Bình");
        var order = Guid.NewGuid();

        await _f.HandleAsync(Paid(userId, 250_000, order));
        await _f.HandleAsync(Paid(userId, 250_000, order));                                         // redelivered
        await _f.HandleAsync(Paid(customerId, 40_000));                                              // named by profile id

        (await BalanceAsync(me)).Should().Be(290);
        await _f.HandleAsync(new OrderPaidIntegrationEvent(_tenant, Guid.NewGuid(), "ORD-2", _b1, null, 90_000, DateTime.UtcNow));   // a walk-in
        await _f.HandleAsync(Paid(userId, 5_000_000, tenant: _otherTenant));                          // another brand does not know this member
        (await BalanceAsync(me)).Should().Be(290);
    }

    [Fact]
    public async Task A_paid_order_nobody_collected_keeps_its_points_but_an_order_the_shop_could_not_make_loses_them()
    {
        var (userId, _, me) = await MemberAsync("Bình");
        var noShow = Guid.NewGuid();
        var unfulfillable = Guid.NewGuid();
        await _f.HandleAsync(Paid(userId, 200_000, noShow));
        await _f.HandleAsync(Paid(userId, 50_000, unfulfillable));

        var noShowEvent = new OrderCancelledIntegrationEvent(_tenant, noShow, _b1, null, "No-show: nobody came", true, [], DateTime.UtcNow, "ORD-1", userId, null, 200_000, "NoShow");
        await _f.HandleAsync(noShowEvent);
        await _f.HandleAsync(noShowEvent);
        (await BalanceAsync(me)).Should().Be(250);                                                   // the shop kept the money, so the points stay

        await _f.HandleAsync(new OrderCancelledIntegrationEvent(_tenant, unfulfillable, _b1, null, "Unfulfillable: out of milk", true, [], DateTime.UtcNow, "ORD-2", userId, null, 50_000, "Unfulfillable"));
        (await BalanceAsync(me)).Should().Be(200);                                                   // everything was refunded, so its points go back
    }

    [Fact]
    public async Task Cancelling_a_paid_order_takes_back_exactly_its_points_once()
    {
        var (userId, _, me) = await MemberAsync("Bình");
        var order = Guid.NewGuid();
        await _f.HandleAsync(Paid(userId, 200_000, order));
        await _f.HandleAsync(Paid(userId, 50_000));
        var cancel = new OrderCancelledIntegrationEvent(_tenant, order, _b1, null, "Wrong drink", true, [], DateTime.UtcNow, "ORD-1", userId, null, 200_000);

        await _f.HandleAsync(cancel);
        await _f.HandleAsync(cancel);                                                                // redelivered

        (await BalanceAsync(me)).Should().Be(50);
        var history = (await me.GetFromJsonAsync<JsonElement>($"{Customers}/me/loyalty")).GetProperty("history").GetProperty("items").EnumerateArray()
            .Select(t => (t.GetProperty("type").GetString(), t.GetProperty("points").GetInt32())).ToList();
        history.Should().BeEquivalentTo(new[] { ("Earn", 200), ("Earn", 50), ("Reverse", -200) });
    }

    [Fact]
    public async Task Points_already_spent_cannot_be_taken_back_below_zero()
    {
        var (userId, _, me) = await MemberAsync("Bình");
        var order = Guid.NewGuid();
        await _f.HandleAsync(Paid(userId, 100_000, order));
        (await me.Post($"{Customers}/me/redemptions", new { points = 80 })).StatusCode.Should().Be(System.Net.HttpStatusCode.Created);

        await _f.HandleAsync(new OrderCancelledIntegrationEvent(_tenant, order, _b1, null, "x", true, [], DateTime.UtcNow, "ORD-1", userId, null, 100_000));

        (await BalanceAsync(me)).Should().Be(0);
    }

    // ------------------------------------------------------------------ voucher around an order (SCRUM-133)
    private async Task<JsonElement> NewVoucherAsync(string code, Guid? branch = null, Guid? issuedTo = null) => await _admin.PostOk(Vouchers, new
    {
        branchId = branch, code, type = "Voucher", discountType = "FixedAmount", value = 20_000, minOrderAmount = 50_000, expiresAt = DateTimeOffset.UtcNow.AddDays(30), issuedToCustomerId = issuedTo,
    });

    [Fact]
    public async Task A_voucher_cancelled_with_its_unpaid_order_is_given_back()
    {
        await NewVoucherAsync("SAVE20");
        (await _cashier.PostOk($"{Vouchers}/redeem", new { code = "SAVE20", orderAmount = 100_000, orderReference = "ORD-77" })).GetProperty("discount").GetDecimal().Should().Be(20_000);
        await _cashier.Post($"{Vouchers}/redeem", new { code = "SAVE20", orderAmount = 100_000, orderReference = "ORD-78" }).ShouldBe(422, "voucher.already_redeemed");

        await _f.HandleAsync(new OrderCancelledIntegrationEvent(_tenant, Guid.NewGuid(), _b1, null, "Not paid in time.", false, [], DateTime.UtcNow, "ORD-OTHER", null, "SAVE20", 100_000));
        await _cashier.Post($"{Vouchers}/redeem", new { code = "SAVE20", orderAmount = 100_000, orderReference = "ORD-78" }).ShouldBe(422, "voucher.already_redeemed");   // another order's cancellation gives nothing back

        await _f.HandleAsync(new OrderCancelledIntegrationEvent(_tenant, Guid.NewGuid(), _b1, null, "Not paid in time.", false, [], DateTime.UtcNow, "ORD-77", null, "SAVE20", 100_000));
        await _f.HandleAsync(new OrderCancelledIntegrationEvent(_tenant, Guid.NewGuid(), _b1, null, "again", false, [], DateTime.UtcNow, "ORD-77", null, "SAVE20", 100_000));

        (await _cashier.PostOk($"{Vouchers}/redeem", new { code = "SAVE20", orderAmount = 100_000, orderReference = "ORD-78" })).GetProperty("discount").GetDecimal().Should().Be(20_000);
    }

    [Fact]
    public async Task A_voucher_of_a_paid_order_that_is_cancelled_afterwards_stays_used()
    {
        await NewVoucherAsync("SAVE20");
        await _cashier.PostOk($"{Vouchers}/redeem", new { code = "SAVE20", orderAmount = 100_000, orderReference = "ORD-77" });

        await _f.HandleAsync(new OrderCancelledIntegrationEvent(_tenant, Guid.NewGuid(), _b1, null, "exception", true, [], DateTime.UtcNow, "ORD-77", null, "SAVE20", 80_000));

        await _cashier.Post($"{Vouchers}/redeem", new { code = "SAVE20", orderAmount = 100_000, orderReference = "ORD-79" }).ShouldBe(422, "voucher.already_redeemed");
    }

    [Fact]
    public async Task A_voucher_for_another_branch_or_another_member_is_refused()
    {
        var (_, mineId, _) = await MemberAsync("Bình");
        var (_, otherId, _) = await MemberAsync("Lan");
        await NewVoucherAsync("ONLYB2", branch: _b2);
        await NewVoucherAsync("MINE", issuedTo: mineId);

        await _admin.Post($"{Vouchers}/redeem", new { code = "ONLYB2", orderAmount = 100_000, orderReference = "ORD-1", branchId = _b1 }).ShouldBe(422, "voucher.wrong_branch");
        await _admin.Post($"{Vouchers}/validate", new { code = "MINE", orderAmount = 100_000 }).ShouldBe(422, "voucher.not_yours");                 // nobody named
        await _admin.Post($"{Vouchers}/validate", new { code = "MINE", orderAmount = 100_000, customerId = otherId }).ShouldBe(422, "voucher.not_yours");
        await _admin.Post($"{Vouchers}/validate", new { code = "MINE", orderAmount = 100_000, customerId = Guid.NewGuid() }).ShouldBe(422, "voucher.not_yours");

        await _admin.PostOk($"{Vouchers}/validate", new { code = "MINE", orderAmount = 100_000, customerId = mineId });
        (await _admin.PostOk($"{Vouchers}/redeem", new { code = "ONLYB2", orderAmount = 100_000, orderReference = "ORD-2", branchId = _b2 })).GetProperty("discount").GetDecimal().Should().Be(20_000);
    }

    // ------------------------------------------------------------------ points to voucher (SCRUM-134)
    [Fact]
    public async Task A_member_turns_points_into_a_voucher_worth_the_brand_rate_and_only_that_member_can_use_it()
    {
        var (userId, customerId, me) = await MemberAsync("Bình");
        await _f.HandleAsync(Paid(userId, 300_000));                                                 // 300 points

        var response = await me.Post($"{Customers}/me/redemptions", new { points = 100 });

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.Created);
        var body = await response.Body();
        (body.GetProperty("pointsSpent").GetInt32(), body.GetProperty("pointsBalance").GetInt32()).Should().Be((100, 200));
        var voucher = body.GetProperty("voucher");
        var code = voucher.GetProperty("code").GetString()!;
        code.Should().StartWith("PT-");
        (voucher.GetProperty("value").GetDecimal(), voucher.GetProperty("discountType").GetString()).Should().Be((100_000m, "FixedAmount"));
        voucher.GetProperty("expiresAtUtc").GetDateTime().Should().BeCloseTo(DateTime.UtcNow.AddDays(90), TimeSpan.FromMinutes(5));

        var mine = await me.GetFromJsonAsync<JsonElement>($"{Customers}/me/vouchers");
        mine.GetProperty("items").EnumerateArray().Single().GetProperty("code").GetString().Should().Be(code);
        (await BalanceAsync(me)).Should().Be(200);

        var quote = await _cashier.PostOk($"{Vouchers}/redeem", new { code, orderAmount = 150_000, orderReference = "ORD-1", branchId = _b1, customerId });
        (quote.GetProperty("discount").GetDecimal(), quote.GetProperty("amountToPay").GetDecimal()).Should().Be((100_000m, 50_000m));
        var (_, strangerId, _) = await MemberAsync("Lan");
        await _cashier.Post($"{Vouchers}/validate", new { code, orderAmount = 150_000, customerId = strangerId }).ShouldBe(422, "voucher.not_yours");
    }

    [Fact]
    public async Task Redemption_follows_the_rules_of_the_brand()
    {
        var (userId, _, me) = await MemberAsync("Bình");
        await _f.HandleAsync(Paid(userId, 50_000));                                                  // 50 points

        await me.Post($"{Customers}/me/redemptions", new { points = 9 }).ShouldBe(422, "loyalty.below_minimum");
        await me.Post($"{Customers}/me/redemptions", new { points = 51 }).ShouldBe(422, "loyalty.insufficient_points");
        await me.Post($"{Customers}/me/redemptions", new { points = 0 }).ShouldBe(400);
        await me.Post($"{Customers}/me/redemptions", new { points = -5 }).ShouldBe(400);
        await me.Post($"{Customers}/me/redemptions", new { }).ShouldBe(400);
        await me.Post($"{Customers}/me/redemptions", new { points = 100_001 }).ShouldBe(400);
        (await BalanceAsync(me)).Should().Be(50);                                                    // nothing was spent by the refusals

        await _admin.Put(Rules, new { isActive = false, amountPerPoint = 1_000, redeemValuePerPoint = 1_000, minPointsToRedeem = 10, silverThreshold = 100, goldThreshold = 500 }).ShouldBe(200);
        await me.Post($"{Customers}/me/redemptions", new { points = 20 }).ShouldBe(422, "loyalty.rules_inactive");
    }

    [Fact]
    public async Task Spending_points_does_not_lower_the_tier()
    {
        var (userId, _, me) = await MemberAsync("Bình");
        await _f.HandleAsync(Paid(userId, 600_000));                                                 // 600 lifetime points: Gold
        await me.PostOk($"{Customers}/me/redemptions", new { points = 590 });

        var loyalty = await me.GetFromJsonAsync<JsonElement>($"{Customers}/me/loyalty");

        (loyalty.GetProperty("pointsBalance").GetInt32(), loyalty.GetProperty("tier").GetString()).Should().Be((10, "Gold"));
    }

    [Fact]
    public async Task A_cashier_redeems_for_a_member_at_the_counter_and_it_is_audited()
    {
        var (userId, customerId, me) = await MemberAsync("Bình");
        await _f.HandleAsync(Paid(userId, 100_000));

        var response = await _cashier.Post($"{Customers}/{customerId}/redemptions", new { points = 40 });

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.Created);
        (await BalanceAsync(me)).Should().Be(60);
        var audit = _f.Events.Published.OfType<AuditRecordedEvent>().Single(e => e.Action == "loyalty.points_redeemed");
        (audit.TenantId, audit.EntityId).Should().Be((_tenant, customerId.ToString()));
        audit.After.Should().Contain("\"pointsSpent\":40");
    }

    [Fact]
    public async Task Redemption_is_limited_by_role_and_brand()
    {
        var (userId, customerId, me) = await MemberAsync("Bình");
        var (_, foreignId, _) = await MemberAsync("Hùng", _otherTenant);
        await _f.HandleAsync(Paid(userId, 100_000));

        await _f.As(AicosRoles.Barista, _b1).Post($"{Customers}/{customerId}/redemptions", new { points = 10 }).ShouldBe(403);
        await _f.As(AicosRoles.Customer).Post($"{Customers}/{customerId}/redemptions", new { points = 10 }).ShouldBe(403);
        await _cashier.Post($"{Customers}/me/redemptions", new { points = 10 }).ShouldBe(403);                                            // staff have no profile
        await _otherAdmin.Post($"{Customers}/{customerId}/redemptions", new { points = 10 }).ShouldBe(404);
        await _cashier.Post($"{Customers}/{foreignId}/redemptions", new { points = 10 }).ShouldBe(404);
        await _f.CreateClient().Post($"{Customers}/me/redemptions", new { points = 10 }).ShouldBe(401);
        await _f.As(AicosRoles.Customer, userId: Guid.NewGuid()).Post($"{Customers}/me/redemptions", new { points = 10 }).ShouldBe(404);   // no profile
        (await _otherAdmin.GetFromJsonAsync<JsonElement>(Vouchers)).GetProperty("totalCount").GetInt32().Should().Be(0);
        (await BalanceAsync(me)).Should().Be(100);
    }

    [Fact]
    public async Task Two_redemptions_cannot_spend_the_same_points_twice()
    {
        var (userId, _, me) = await MemberAsync("Bình");
        await _f.HandleAsync(Paid(userId, 100_000));

        await me.PostOk($"{Customers}/me/redemptions", new { points = 60 });
        await me.Post($"{Customers}/me/redemptions", new { points = 60 }).ShouldBe(422, "loyalty.insufficient_points");

        (await BalanceAsync(me)).Should().Be(40);
        (await me.GetFromJsonAsync<JsonElement>($"{Customers}/me/vouchers")).GetProperty("totalCount").GetInt32().Should().Be(1);
    }
}
