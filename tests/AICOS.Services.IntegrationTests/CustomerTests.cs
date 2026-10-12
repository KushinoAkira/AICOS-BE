using System.Net.Http.Json;
using System.Text.Json;
using AICOS.BuildingBlocks.Security;
using AICOS.Contracts.Events;
using AICOS.CustomerEngagement.Api;
using AICOS.CustomerEngagement.Api.Persistence;

namespace AICOS.Services.IntegrationTests;

public sealed class CustomerTests : IDisposable
{
    private readonly ServiceFactory<CustomerEngagementService, CustomerEngagementDbContext> _f = new();
    private readonly Guid _branch = Guid.NewGuid();
    private readonly HttpClient _manager;
    private readonly HttpClient _cashier;

    public CustomerTests()
    {
        _manager = _f.As(AicosRoles.Manager, _branch);
        _cashier = _f.As(AicosRoles.Cashier, _branch);
    }

    public void Dispose() => _f.Dispose();

    [Fact]
    public async Task Registration_event_creates_profile_once_and_customer_edits_it()
    {
        var userId = Guid.NewGuid();
        await _f.HandleAsync(new CustomerRegisteredEvent(userId, "Nguyễn An", "An@Example.com", "0901234567"));
        await _f.HandleAsync(new CustomerRegisteredEvent(userId, "Nguyễn An", "An@Example.com", "0901234567"));

        var me = _f.As(AicosRoles.Customer, userId: userId);
        var profile = await me.GetFromJsonAsync<JsonElement>("/api/v1/customers/me");
        profile.GetProperty("email").GetString().Should().Be("an@example.com");
        profile.GetProperty("marketingOptIn").GetBoolean().Should().BeFalse();

        await me.Put("/api/v1/customers/me", new { fullName = "", birthday = "2999-01-01" }).ShouldBe(400);
        await me.Put("/api/v1/customers/me", new { fullName = "An Nguyễn", phone = "0911111111", birthday = "2000-05-20", marketingOptIn = true }).ShouldBe(200);

        (await _cashier.GetFromJsonAsync<JsonElement>("/api/v1/customers?search=0911")).GetProperty("totalCount").GetInt32().Should().Be(1);
        await _f.As(AicosRoles.Customer).GetAsync("/api/v1/customers/me").ShouldBe(404);
    }

    [Fact]
    public async Task Completed_order_awards_points_once_BR25_and_updates_tier()
    {
        var userId = Guid.NewGuid();
        await _f.HandleAsync(new CustomerRegisteredEvent(userId, "Bình", "binh@example.com", null));
        var order = Guid.NewGuid();
        var completed = new OrderCompletedEvent(order, _branch, userId, 12_345_000m, []);

        await _f.HandleAsync(completed);
        await _f.HandleAsync(completed); // redelivered: ignored

        var loyalty = await _f.As(AicosRoles.Customer, userId: userId).GetFromJsonAsync<JsonElement>("/api/v1/customers/me/loyalty");
        loyalty.GetProperty("pointsBalance").GetInt32().Should().Be(1234);
        loyalty.GetProperty("tier").GetString().Should().Be("Silver");
        loyalty.GetProperty("history").GetProperty("totalCount").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task Manual_adjustment_cannot_make_balance_negative()
    {
        var userId = Guid.NewGuid();
        await _f.HandleAsync(new CustomerRegisteredEvent(userId, "Chi", "chi@example.com", null));
        var customerId = (await _f.As(AicosRoles.Customer, userId: userId).GetFromJsonAsync<JsonElement>("/api/v1/customers/me")).Id();

        await _manager.Post($"/api/v1/customers/{customerId}/points-adjustments", new { points = 50, note = "" }).ShouldBe(400);
        await _manager.Post($"/api/v1/customers/{customerId}/points-adjustments", new { points = 50, note = "Bù lỗi hệ thống" }).ShouldBe(200);
        await _manager.Post($"/api/v1/customers/{customerId}/points-adjustments", new { points = -80, note = "Trừ" }).ShouldBe(422, "loyalty.insufficient_points");
        await _cashier.Post($"/api/v1/customers/{customerId}/points-adjustments", new { points = 1, note = "x" }).ShouldBe(403);
    }

    [Fact]
    public async Task Voucher_validate_redeem_and_cannot_be_reused_BR35()
    {
        var voucher = await _manager.PostOk("/api/v1/customers/vouchers", new
        {
            code = "sale20", type = "Voucher", discountType = "Percentage", value = 20, minOrderAmount = 100000, expiresAt = _f.Clock.Now.AddDays(30),
        });
        voucher.GetProperty("code").GetString().Should().Be("SALE20");

        await _cashier.Post("/api/v1/customers/vouchers/validate", new { code = "SALE20", orderAmount = 50000 }).ShouldBe(422, "voucher.min_order_not_met");
        var quote = await _cashier.PostOk("/api/v1/customers/vouchers/validate", new { code = "sale20", orderAmount = 200000 });
        quote.GetProperty("discount").GetDecimal().Should().Be(40000);

        await _cashier.PostOk("/api/v1/customers/vouchers/redeem", new { code = "SALE20", orderAmount = 200000, orderReference = "HD-001" });
        await _cashier.Post("/api/v1/customers/vouchers/redeem", new { code = "SALE20", orderAmount = 200000, orderReference = "HD-002" }).ShouldBe(422, "voucher.already_redeemed");
        await _f.As(AicosRoles.Cashier, Guid.NewGuid()).Post("/api/v1/customers/vouchers/validate", new { code = "SALE20", orderAmount = 200000 }).ShouldBe(404);
    }

    [Fact]
    public async Task Gift_card_expired_revoked_and_validation_rules()
    {
        await _manager.Post("/api/v1/customers/vouchers", new { type = "Voucher", discountType = "Percentage", value = 150, expiresAt = _f.Clock.Now.AddDays(1) }).ShouldBe(400);
        await _manager.Post("/api/v1/customers/vouchers", new { type = "GiftCard", value = 100000, expiresAt = _f.Clock.Now.AddDays(-1) }).ShouldBe(400);

        var gift = await _manager.PostOk("/api/v1/customers/vouchers", new { type = "GiftCard", value = 100000, expiresAt = _f.Clock.Now.AddDays(1), issuedToName = "Nhân viên A" });
        gift.GetProperty("code").GetString().Should().StartWith("GC-");
        await _manager.Post("/api/v1/customers/vouchers", new { code = gift.GetProperty("code").GetString(), type = "GiftCard", value = 1, expiresAt = _f.Clock.Now.AddDays(1) })
            .ShouldBe(409, "voucher.duplicate_code");

        var quote = await _cashier.PostOk("/api/v1/customers/vouchers/validate", new { code = gift.GetProperty("code").GetString(), orderAmount = 60000 });
        quote.GetProperty("amountToPay").GetDecimal().Should().Be(0);

        _f.Clock.Now = _f.Clock.Now.AddDays(2);
        await _cashier.Post("/api/v1/customers/vouchers/validate", new { code = gift.GetProperty("code").GetString(), orderAmount = 60000 }).ShouldBe(422, "voucher.expired");

        var other = await _manager.PostOk("/api/v1/customers/vouchers", new { type = "Voucher", discountType = "FixedAmount", value = 10000, expiresAt = _f.Clock.Now.AddDays(5) });
        await _manager.Post($"/api/v1/customers/vouchers/{other.Id()}/revoke", new { reason = "Phát nhầm" }).ShouldBe(200);
        await _cashier.Post("/api/v1/customers/vouchers/validate", new { code = other.GetProperty("code").GetString(), orderAmount = 60000 }).ShouldBe(422, "voucher.revoked");
    }
}
