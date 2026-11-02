using System.Net.Http.Json;
using System.Text.Json;
using AICOS.BuildingBlocks.Security;
using AICOS.Contracts.Events;
using AICOS.CustomerEngagement.Api;
using AICOS.CustomerEngagement.Api.Persistence;

namespace AICOS.Services.IntegrationTests;

/// <summary>
/// Customers and loyalty (UC-16 rules, BR-44/47 points belong to the brand), vouchers (UC-17, BR-46) and tenant isolation (BR-04).
/// </summary>
public sealed class CustomerTests : IDisposable
{
    private const string Customers = "/api/v1/customers";
    private const string Vouchers = "/api/v1/customers/vouchers";
    private const string Rules = "/api/v1/customers/loyalty/rules";

    private readonly ServiceFactory<CustomerEngagementService, CustomerEngagementDbContext> _f = new();
    private readonly Guid _tenant = ServiceFactory<CustomerEngagementService, CustomerEngagementDbContext>.DefaultTenantId;
    private readonly Guid _otherTenant = Guid.NewGuid();
    private readonly Guid _b1;
    private readonly Guid _b2;
    private readonly Guid _otherBranch;
    private readonly HttpClient _admin;
    private readonly HttpClient _otherAdmin;
    private readonly HttpClient _manager;
    private readonly HttpClient _cashier;

    public CustomerTests()
    {
        _b1 = _f.Branches.Add(_tenant, name: "Quận 1");
        _b2 = _f.Branches.Add(_tenant, name: "Quận 3");
        _otherBranch = _f.Branches.Add(_otherTenant, name: "Foreign");
        _admin = _f.As(AicosRoles.BrandAdmin);
        _otherAdmin = _f.As(AicosRoles.BrandAdmin, tenantId: _otherTenant);
        _manager = _f.As(AicosRoles.BranchManager, _b1);
        _cashier = _f.As(AicosRoles.Cashier, _b1);
    }

    public void Dispose() => _f.Dispose();

    private OrderPaidIntegrationEvent Paid(Guid customer, decimal amount, Guid? tenant = null, Guid? order = null) =>
        new(tenant ?? _tenant, order ?? Guid.NewGuid(), "ORD-TEST", _b1, customer, amount, DateTime.UtcNow);

    private async Task<Guid> RegisterAsync(string name, string email, Guid? tenant = null, Guid? userId = null)
    {
        var id = userId ?? Guid.NewGuid();
        await _f.HandleAsync(new CustomerRegisteredEvent(id, name, email, null, tenant ?? _tenant));
        return id;
    }

    private async Task<Guid> CustomerIdAsync(Guid userId, Guid? tenant = null) =>
        (await _f.As(AicosRoles.Customer, userId: userId, tenantId: tenant).GetFromJsonAsync<JsonElement>($"{Customers}/me")).Id();

    // ------------------------------------------------------------------ profile
    [Fact]
    public async Task Registration_event_creates_the_profile_once_in_the_brand_and_the_customer_edits_it()
    {
        var userId = Guid.NewGuid();
        await _f.HandleAsync(new CustomerRegisteredEvent(userId, "Nguyễn An", "An@Example.com", "0901234567", _tenant));
        await _f.HandleAsync(new CustomerRegisteredEvent(userId, "Nguyễn An", "An@Example.com", "0901234567", _tenant));

        var me = _f.As(AicosRoles.Customer, userId: userId);
        var profile = await me.GetFromJsonAsync<JsonElement>($"{Customers}/me");
        profile.GetProperty("email").GetString().Should().Be("an@example.com");
        profile.GetProperty("marketingOptIn").GetBoolean().Should().BeFalse();

        await me.Put($"{Customers}/me", new { fullName = "", birthday = "2999-01-01" }).ShouldBe(400);
        await me.Put($"{Customers}/me", new { fullName = "An Nguyễn", phone = "0911111111", birthday = "2000-05-20", marketingOptIn = true }).ShouldBe(200);

        (await _cashier.GetFromJsonAsync<JsonElement>($"{Customers}?search=0911")).GetProperty("totalCount").GetInt32().Should().Be(1);
        await _f.As(AicosRoles.Customer).GetAsync($"{Customers}/me").ShouldBe(404);
    }

    [Fact]
    public async Task Staff_see_only_the_customers_of_their_own_brand_and_get_404_for_the_others_BR04()
    {
        var mine = await RegisterAsync("Bình", "binh@example.com");
        await RegisterAsync("Lan", "lan@example.com", _otherTenant);
        var myCustomer = await CustomerIdAsync(mine);

        (await _cashier.GetFromJsonAsync<JsonElement>(Customers)).GetProperty("totalCount").GetInt32().Should().Be(1);
        (await _cashier.GetFromJsonAsync<JsonElement>($"{Customers}?search=lan")).GetProperty("totalCount").GetInt32().Should().Be(0);
        (await _otherAdmin.GetFromJsonAsync<JsonElement>(Customers)).GetProperty("items")[0].GetProperty("email").GetString().Should().Be("lan@example.com");

        await _otherAdmin.GetAsync($"{Customers}/{myCustomer}").ShouldBe(404);
        await _otherAdmin.GetAsync($"{Customers}/{myCustomer}/loyalty").ShouldBe(404);
        await _otherAdmin.Post($"{Customers}/{myCustomer}/points-adjustments", new { points = 10, note = "x" }).ShouldBe(404);
        await _f.As(AicosRoles.SuperAdmin).GetAsync(Customers).ShouldBe(403);
        await _f.As(AicosRoles.Customer).GetAsync(Customers).ShouldBe(403);
    }

    // ------------------------------------------------------------------ loyalty rules (UC-16)
    [Fact]
    public async Task A_brand_that_never_configured_loyalty_gets_the_platform_defaults()
    {
        var rules = await _admin.GetFromJsonAsync<JsonElement>(Rules);

        rules.GetProperty("isDefault").GetBoolean().Should().BeTrue();
        rules.GetProperty("isActive").GetBoolean().Should().BeTrue();
        rules.GetProperty("amountPerPoint").GetDecimal().Should().Be(10_000);
        rules.GetProperty("redeemValuePerPoint").GetDecimal().Should().Be(100);
        rules.GetProperty("silverThreshold").GetInt32().Should().Be(1_000);
        rules.GetProperty("goldThreshold").GetInt32().Should().Be(5_000);
    }

    [Fact]
    public async Task The_brand_admin_sets_the_rules_and_only_that_brand_is_affected()
    {
        var response = await _admin.Put(Rules, NewRules(amountPerPoint: 5_000, redeemValue: 200, minPoints: 20, silver: 100, gold: 300));

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        var body = await response.Body();
        body.GetProperty("isDefault").GetBoolean().Should().BeFalse();
        body.GetProperty("amountPerPoint").GetDecimal().Should().Be(5_000);

        (await _admin.GetFromJsonAsync<JsonElement>(Rules)).GetProperty("minPointsToRedeem").GetInt32().Should().Be(20);
        (await _manager.GetFromJsonAsync<JsonElement>(Rules)).GetProperty("amountPerPoint").GetDecimal().Should().Be(5_000);
        (await _otherAdmin.GetFromJsonAsync<JsonElement>(Rules)).GetProperty("isDefault").GetBoolean().Should().BeTrue();

        await _admin.Put(Rules, NewRules(amountPerPoint: 8_000, redeemValue: 100, minPoints: 10, silver: 50, gold: 80)).ShouldBe(200);   // a second save replaces
        (await _admin.GetFromJsonAsync<JsonElement>(Rules)).GetProperty("amountPerPoint").GetDecimal().Should().Be(8_000);
    }

    [Fact]
    public async Task Loyalty_rules_are_validated_and_only_the_brand_admin_can_change_them()
    {
        await _admin.Put(Rules, new { }).ShouldBe(400);
        await _admin.Put(Rules, NewRules(amountPerPoint: 0)).ShouldBe(400);
        await _admin.Put(Rules, NewRules(redeemValue: 0)).ShouldBe(400);
        await _admin.Put(Rules, NewRules(minPoints: 0)).ShouldBe(400);
        await _admin.Put(Rules, NewRules(silver: 0)).ShouldBe(400);
        await _admin.Put(Rules, NewRules(silver: 500, gold: 500)).ShouldBe(400);                  // gold must be above silver
        await _admin.Put(Rules, NewRules(silver: 600, gold: 500)).ShouldBe(400);
        await _admin.Put(Rules, NewRules(amountPerPoint: 100_000_001)).ShouldBe(400);

        await _manager.Put(Rules, NewRules()).ShouldBe(403);
        await _cashier.Put(Rules, NewRules()).ShouldBe(403);
        await _cashier.GetAsync(Rules).ShouldBe(403);
        await _f.As(AicosRoles.Customer).GetAsync(Rules).ShouldBe(403);
        await _f.As(AicosRoles.SuperAdmin).GetAsync(Rules).ShouldBe(403);
        await _f.CreateClient().GetAsync(Rules).ShouldBe(401);
    }

    [Fact]
    public async Task Points_follow_the_brand_rule_BR44_and_a_paused_programme_awards_none()
    {
        var mine = await RegisterAsync("Bình", "binh@example.com");
        var theirs = await RegisterAsync("Lan", "lan@example.com", _otherTenant);
        await _admin.Put(Rules, NewRules(amountPerPoint: 1_000, silver: 100, gold: 200)).ShouldBe(200);

        await _f.HandleAsync(Paid(mine, 150_000m));
        await _f.HandleAsync(Paid(theirs, 150_000m, _otherTenant));      // the other brand keeps the default rule

        var loyalty = await _f.As(AicosRoles.Customer, userId: mine).GetFromJsonAsync<JsonElement>($"{Customers}/me/loyalty");
        loyalty.GetProperty("pointsBalance").GetInt32().Should().Be(150);
        loyalty.GetProperty("tier").GetString().Should().Be("Silver");
        (await _f.As(AicosRoles.Customer, userId: theirs, tenantId: _otherTenant).GetFromJsonAsync<JsonElement>($"{Customers}/me/loyalty"))
            .GetProperty("pointsBalance").GetInt32().Should().Be(15);

        await _admin.Put(Rules, NewRules(active: false, amountPerPoint: 1_000)).ShouldBe(200);
        await _f.HandleAsync(Paid(mine, 500_000m));
        (await _f.As(AicosRoles.Customer, userId: mine).GetFromJsonAsync<JsonElement>($"{Customers}/me/loyalty"))
            .GetProperty("pointsBalance").GetInt32().Should().Be(150); // nothing new while the programme is paused
    }

    [Fact]
    public async Task Changing_the_thresholds_moves_existing_customers_to_their_new_tier()
    {
        var userId = await RegisterAsync("Bình", "binh@example.com");
        await _f.HandleAsync(Paid(userId, 5_000_000m)); // 500 points with the default rule
        var me = _f.As(AicosRoles.Customer, userId: userId);
        (await me.GetFromJsonAsync<JsonElement>($"{Customers}/me")).GetProperty("tier").GetString().Should().Be("Member");

        await _admin.Put(Rules, NewRules(silver: 100, gold: 400)).ShouldBe(200);
        (await me.GetFromJsonAsync<JsonElement>($"{Customers}/me")).GetProperty("tier").GetString().Should().Be("Gold");

        await _admin.Put(Rules, NewRules(silver: 1_000, gold: 2_000)).ShouldBe(200);
        (await me.GetFromJsonAsync<JsonElement>($"{Customers}/me")).GetProperty("tier").GetString().Should().Be("Member");
        (await me.GetFromJsonAsync<JsonElement>($"{Customers}/me/loyalty")).GetProperty("minPointsToRedeem").GetInt32().Should().BeGreaterThan(0);
        (await me.GetFromJsonAsync<JsonElement>($"{Customers}/me/loyalty")).GetProperty("nextTierAt").GetInt32().Should().Be(1_000);
    }

    [Fact]
    public async Task A_paid_order_awards_points_once_with_the_default_rule_and_updates_the_tier()
    {
        var userId = await RegisterAsync("Bình", "binh@example.com");
        var completed = Paid(userId, 12_345_000m);

        await _f.HandleAsync(completed);
        await _f.HandleAsync(completed); // redelivered: ignored

        var loyalty = await _f.As(AicosRoles.Customer, userId: userId).GetFromJsonAsync<JsonElement>($"{Customers}/me/loyalty");
        loyalty.GetProperty("pointsBalance").GetInt32().Should().Be(1234);
        loyalty.GetProperty("tier").GetString().Should().Be("Silver");
        loyalty.GetProperty("history").GetProperty("totalCount").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task Rule_changes_are_sent_to_the_audit_history()
    {
        await _admin.Put(Rules, NewRules(amountPerPoint: 5_000)).ShouldBe(200);

        var audit = _f.Events.Published.OfType<AuditRecordedEvent>().Single();
        audit.Action.Should().Be("loyalty_rule.updated");
        audit.TenantId.Should().Be(_tenant);
        audit.Before.Should().Contain("\"isDefault\":true");
        audit.After.Should().Contain("\"amountPerPoint\":5000").And.Contain("\"isDefault\":false");
    }

    // ------------------------------------------------------------------ manual points adjustment
    [Fact]
    public async Task Manual_adjustment_cannot_make_the_balance_negative_and_is_audited()
    {
        var userId = await RegisterAsync("Chi", "chi@example.com");
        var customerId = await CustomerIdAsync(userId);

        await _manager.Post($"{Customers}/{customerId}/points-adjustments", new { points = 50, note = "" }).ShouldBe(400);
        await _manager.Post($"{Customers}/{customerId}/points-adjustments", new { points = 0, note = "x" }).ShouldBe(400);
        await _manager.Post($"{Customers}/{customerId}/points-adjustments", new { points = 50, note = "Bù lỗi hệ thống" }).ShouldBe(200);
        await _manager.Post($"{Customers}/{customerId}/points-adjustments", new { points = -80, note = "Trừ" }).ShouldBe(422, "loyalty.insufficient_points");
        await _cashier.Post($"{Customers}/{customerId}/points-adjustments", new { points = 1, note = "x" }).ShouldBe(403);

        var audit = _f.Events.Published.OfType<AuditRecordedEvent>().Single();
        audit.Action.Should().Be("points.adjusted");
        audit.EntityType.Should().Be("Customer");
        audit.TenantId.Should().Be(_tenant);
        audit.ActorRole.Should().Be(AicosRoles.BranchManager);
        audit.After.Should().Contain("\"pointsBalance\":50");
        JsonDocument.Parse(audit.After!).RootElement.GetProperty("reason").GetString().Should().Be("Bù lỗi hệ thống"); // the JSON escapes accents
    }

    // ------------------------------------------------------------------ vouchers (UC-17, BR-46)
    [Fact]
    public async Task Voucher_validate_redeem_and_cannot_be_reused_BR46()
    {
        var voucher = await _admin.PostOk(Vouchers, new
        {
            code = "sale20", type = "Voucher", discountType = "Percentage", value = 20, minOrderAmount = 100000, expiresAt = _f.Clock.Now.AddDays(30),
        });
        voucher.GetProperty("code").GetString().Should().Be("SALE20");
        voucher.GetProperty("branchId").ValueKind.Should().Be(JsonValueKind.Null); // valid at every branch

        await _cashier.Post($"{Vouchers}/validate", new { code = "SALE20", orderAmount = 50000 }).ShouldBe(422, "voucher.min_order_not_met");
        var quote = await _cashier.PostOk($"{Vouchers}/validate", new { code = "sale20", orderAmount = 200000 });
        quote.GetProperty("discount").GetDecimal().Should().Be(40000);

        await _cashier.PostOk($"{Vouchers}/redeem", new { code = "SALE20", orderAmount = 200000, orderReference = "HD-001" });
        await _cashier.Post($"{Vouchers}/redeem", new { code = "SALE20", orderAmount = 200000, orderReference = "HD-002" }).ShouldBe(422, "voucher.already_redeemed");
        await _f.As(AicosRoles.Cashier, _b2).Post($"{Vouchers}/validate", new { code = "SALE20", orderAmount = 200000 }).ShouldBe(422, "voucher.already_redeemed"); // used once, anywhere
    }

    [Fact]
    public async Task A_voucher_for_one_branch_cannot_be_used_or_seen_at_another()
    {
        var voucher = await _admin.PostOk(Vouchers, new
        {
            branchId = _b1, code = "Q1ONLY", type = "Voucher", discountType = "FixedAmount", value = 10000, expiresAt = _f.Clock.Now.AddDays(30),
        });

        await _cashier.PostOk($"{Vouchers}/validate", new { code = "Q1ONLY", orderAmount = 60000 });
        await _f.As(AicosRoles.Cashier, _b2).Post($"{Vouchers}/validate", new { code = "Q1ONLY", orderAmount = 60000 }).ShouldBe(404);
        await _f.As(AicosRoles.Cashier, _b2).Post($"{Vouchers}/redeem", new { code = "Q1ONLY", orderAmount = 60000, orderReference = "HD" }).ShouldBe(404);
        await _admin.Post($"{Vouchers}/validate", new { code = "Q1ONLY", orderAmount = 60000 }).ShouldBe(200);            // the brand admin works anywhere

        await _manager.GetAsync($"{Vouchers}/{voucher.Id()}").ShouldBe(200);
        await _f.As(AicosRoles.BranchManager, _b2).GetAsync($"{Vouchers}/{voucher.Id()}").ShouldBe(404);
    }

    [Fact]
    public async Task Voucher_lists_are_scoped_a_manager_sees_their_branch_and_the_brand_wide_ones()
    {
        await _admin.PostOk(Vouchers, new { branchId = _b1, code = "ONLYQ1", type = "Voucher", discountType = "FixedAmount", value = 5000, expiresAt = _f.Clock.Now.AddDays(5) });
        await _admin.PostOk(Vouchers, new { branchId = _b2, code = "ONLYQ3", type = "Voucher", discountType = "FixedAmount", value = 5000, expiresAt = _f.Clock.Now.AddDays(5) });
        await _admin.PostOk(Vouchers, new { code = "EVERYWHERE", type = "Voucher", discountType = "FixedAmount", value = 5000, expiresAt = _f.Clock.Now.AddDays(5) });
        await _otherAdmin.PostOk(Vouchers, new { code = "THEIRS", type = "Voucher", discountType = "FixedAmount", value = 5000, expiresAt = _f.Clock.Now.AddDays(5) });

        Codes(await _admin.GetFromJsonAsync<JsonElement>(Vouchers)).Should().BeEquivalentTo("ONLYQ1", "ONLYQ3", "EVERYWHERE");
        Codes(await _manager.GetFromJsonAsync<JsonElement>(Vouchers)).Should().BeEquivalentTo("ONLYQ1", "EVERYWHERE");
        Codes(await _admin.GetFromJsonAsync<JsonElement>($"{Vouchers}?branchId={_b2}")).Should().BeEquivalentTo("ONLYQ3");
        Codes(await _otherAdmin.GetFromJsonAsync<JsonElement>(Vouchers)).Should().BeEquivalentTo("THEIRS");
        Codes(await _admin.GetFromJsonAsync<JsonElement>($"{Vouchers}?search=every")).Should().BeEquivalentTo("EVERYWHERE");
        await _cashier.GetAsync(Vouchers).ShouldBe(403);
    }

    [Fact]
    public async Task Another_brand_cannot_use_see_or_revoke_a_voucher_and_the_same_code_can_exist_in_both_brands()
    {
        var mine = await _admin.PostOk(Vouchers, new { code = "WELCOME", type = "Voucher", discountType = "FixedAmount", value = 10000, expiresAt = _f.Clock.Now.AddDays(30) });
        var otherStaff = _f.As(AicosRoles.Cashier, _otherBranch, tenantId: _otherTenant);

        await otherStaff.Post($"{Vouchers}/validate", new { code = "WELCOME", orderAmount = 60000 }).ShouldBe(404);
        await _otherAdmin.GetAsync($"{Vouchers}/{mine.Id()}").ShouldBe(404);
        await _otherAdmin.Post($"{Vouchers}/{mine.Id()}/revoke", new { reason = "x" }).ShouldBe(404);

        await _otherAdmin.PostOk(Vouchers, new { code = "WELCOME", type = "Voucher", discountType = "FixedAmount", value = 99, expiresAt = _f.Clock.Now.AddDays(30) });
        (await otherStaff.PostOk($"{Vouchers}/validate", new { code = "WELCOME", orderAmount = 60000 })).GetProperty("discount").GetDecimal().Should().Be(99);
        (await _cashier.PostOk($"{Vouchers}/validate", new { code = "WELCOME", orderAmount = 60000 })).GetProperty("discount").GetDecimal().Should().Be(10000);
    }

    [Fact]
    public async Task Only_the_brand_admin_issues_and_revokes_vouchers()
    {
        var voucher = await _admin.PostOk(Vouchers, new { code = "WELCOME", type = "Voucher", discountType = "FixedAmount", value = 10000, expiresAt = _f.Clock.Now.AddDays(30) });
        var body = new { type = "Voucher", discountType = "FixedAmount", value = 1000, expiresAt = _f.Clock.Now.AddDays(1) };

        await _manager.Post(Vouchers, body).ShouldBe(403);
        await _cashier.Post(Vouchers, body).ShouldBe(403);
        await _manager.Post($"{Vouchers}/{voucher.Id()}/revoke", new { reason = "x" }).ShouldBe(403);
        await _f.As(AicosRoles.Customer).Post(Vouchers, body).ShouldBe(403);
        await _f.As(AicosRoles.SuperAdmin).Post(Vouchers, body).ShouldBe(403);
        await _f.CreateClient().PostAsJsonAsync(Vouchers, body).ShouldBe(401);
    }

    [Fact]
    public async Task A_voucher_can_only_target_a_branch_and_a_customer_of_the_brand()
    {
        var foreignCustomer = await RegisterAsync("Lan", "lan@example.com", _otherTenant);
        var foreignCustomerId = await CustomerIdAsync(foreignCustomer, _otherTenant);
        var expires = _f.Clock.Now.AddDays(5);

        await _admin.Post(Vouchers, new { branchId = _otherBranch, type = "Voucher", discountType = "FixedAmount", value = 1000, expiresAt = expires }).ShouldBe(400);
        await _admin.Post(Vouchers, new { branchId = Guid.NewGuid(), type = "Voucher", discountType = "FixedAmount", value = 1000, expiresAt = expires }).ShouldBe(400);
        await _admin.Post(Vouchers, new { issuedToCustomerId = foreignCustomerId, type = "Voucher", discountType = "FixedAmount", value = 1000, expiresAt = expires }).ShouldBe(400);

        var mine = await CustomerIdAsync(await RegisterAsync("Bình", "binh@example.com"));
        await _admin.Post(Vouchers, new { issuedToCustomerId = mine, issuedToName = "Bình", type = "Voucher", discountType = "FixedAmount", value = 1000, expiresAt = expires }).ShouldBe(201);
    }

    [Fact]
    public async Task Gift_card_expired_revoked_and_validation_rules()
    {
        await _admin.Post(Vouchers, new { type = "Voucher", discountType = "Percentage", value = 150, expiresAt = _f.Clock.Now.AddDays(1) }).ShouldBe(400);
        await _admin.Post(Vouchers, new { type = "GiftCard", value = 100000, expiresAt = _f.Clock.Now.AddDays(-1) }).ShouldBe(400);
        await _admin.Post(Vouchers, new { type = "Voucher", discountType = "FixedAmount", value = 0, expiresAt = _f.Clock.Now.AddDays(1) }).ShouldBe(400);
        await _admin.Post(Vouchers, new { code = "a b", type = "GiftCard", value = 1000, expiresAt = _f.Clock.Now.AddDays(1) }).ShouldBe(400);
        await _admin.Post(Vouchers, new { type = "GiftCard", value = 1000, expiresAt = _f.Clock.Now.AddYears(3) }).ShouldBe(400);

        var gift = await _admin.PostOk(Vouchers, new { type = "GiftCard", value = 100000, expiresAt = _f.Clock.Now.AddDays(1), issuedToName = "Nhân viên A" });
        gift.GetProperty("code").GetString().Should().StartWith("GC-");
        await _admin.Post(Vouchers, new { code = gift.GetProperty("code").GetString(), type = "GiftCard", value = 1, expiresAt = _f.Clock.Now.AddDays(1) })
            .ShouldBe(409, "voucher.duplicate_code");

        var quote = await _cashier.PostOk($"{Vouchers}/validate", new { code = gift.GetProperty("code").GetString(), orderAmount = 60000 });
        quote.GetProperty("amountToPay").GetDecimal().Should().Be(0);

        _f.Clock.Now = _f.Clock.Now.AddDays(2);
        await _cashier.Post($"{Vouchers}/validate", new { code = gift.GetProperty("code").GetString(), orderAmount = 60000 }).ShouldBe(422, "voucher.expired");

        var other = await _admin.PostOk(Vouchers, new { type = "Voucher", discountType = "FixedAmount", value = 10000, expiresAt = _f.Clock.Now.AddDays(5) });
        await _admin.Post($"{Vouchers}/{other.Id()}/revoke", new { reason = "" }).ShouldBe(400);
        await _admin.Post($"{Vouchers}/{other.Id()}/revoke", new { reason = "Phát nhầm" }).ShouldBe(200);
        await _admin.Post($"{Vouchers}/{other.Id()}/revoke", new { reason = "Lần hai" }).ShouldBe(422, "voucher.invalid_status");
        await _cashier.Post($"{Vouchers}/validate", new { code = other.GetProperty("code").GetString(), orderAmount = 60000 }).ShouldBe(422, "voucher.revoked");
    }

    [Fact]
    public async Task Voucher_creation_revocation_and_redemption_are_sent_to_the_audit_history()
    {
        var voucher = await _admin.PostOk(Vouchers, new { code = "WELCOME", type = "Voucher", discountType = "FixedAmount", value = 10000, expiresAt = _f.Clock.Now.AddDays(30) });
        await _cashier.PostOk($"{Vouchers}/redeem", new { code = "WELCOME", orderAmount = 60000, orderReference = "HD-1" });
        var second = await _admin.PostOk(Vouchers, new { code = "OTHER", type = "Voucher", discountType = "FixedAmount", value = 5000, expiresAt = _f.Clock.Now.AddDays(30) });
        await _admin.Post($"{Vouchers}/{second.Id()}/revoke", new { reason = "Phát nhầm" }).ShouldBe(200);

        var events = _f.Events.Published.OfType<AuditRecordedEvent>().Where(e => e.EntityType == "Voucher").ToList();

        events.Select(e => e.Action).Should().Equal("voucher.created", "voucher.redeemed", "voucher.created", "voucher.revoked");
        events.Should().OnlyContain(e => e.TenantId == _tenant);
        events[1].ActorRole.Should().Be(AicosRoles.Cashier);
        events[1].EntityId.Should().Be(voucher.Id().ToString());
        events[1].Before.Should().Contain("\"status\":\"Active\"");
        events[1].After.Should().Contain("\"status\":\"Redeemed\"").And.Contain("HD-1");
    }

    // ------------------------------------------------------------------ helpers
    private static object NewRules(bool active = true, decimal amountPerPoint = 10_000, decimal redeemValue = 100, int minPoints = 50, int silver = 1_000, int gold = 5_000) => new
    {
        isActive = active, amountPerPoint, redeemValuePerPoint = redeemValue, minPointsToRedeem = minPoints, silverThreshold = silver, goldThreshold = gold,
    };

    private static List<string?> Codes(JsonElement page) => page.GetProperty("items").EnumerateArray().Select(v => v.GetProperty("code").GetString()).ToList();
}
