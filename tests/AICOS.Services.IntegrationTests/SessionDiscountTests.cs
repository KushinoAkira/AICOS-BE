using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AICOS.BuildingBlocks.Exceptions;
using AICOS.BuildingBlocks.Security;
using AICOS.Ordering.Api.Domain.Entities;
using AICOS.Ordering.Api.Domain.Enums;
using AICOS.Ordering.Api.Features.SessionBilling;
using AICOS.Ordering.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AICOS.Services.IntegrationTests;

public sealed class DiscountActorDouble : IDiscountActorVerifier
{
    public bool Allowed = true;
    public Task EnsureAsync(ICurrentUser user, CancellationToken ct)
    {
        if (!Allowed) throw new ForbiddenException("Test actor revoked.", "discount.actor_changed");
        return Task.CompletedTask;
    }
}

/// <summary>HTTP handlers with EF InMemory, test JWT and explicit service/actor doubles; not PostgreSQL E2E.</summary>
public sealed class SessionDiscountTests : IAsyncLifetime
{
    private readonly DiscountActorDouble _actor = new();
    private readonly SessionBillHarness _h = new() { EnableSessionDiscount = true };
    public async Task InitializeAsync() { _h.DiscountActor = _actor; await _h.InitializeAsync(); await _h.RoundAsync(); await _h.RoundAsync(); }
    public Task DisposeAsync() { _h.Dispose(); return Task.CompletedTask; }
    private string Path => $"/api/v1/orders/qr/sessions/{_h.Session}/manual-discount";
    private Task<SessionDiscountView?> Read() => _h.OrderCashier.GetFromJsonAsync<SessionDiscountView>(Path);
    public static Task<HttpResponseMessage> Set(HttpClient client, Guid session, SessionDiscountView view,
        decimal value, string mode = "AMOUNT", string? key = null, string reason = "Khách hàng thân thiết")
    {
        var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/orders/qr/sessions/{session}/manual-discount")
        { Content = JsonContent.Create(new SetSessionDiscount(view.Fingerprint, view.Revision, mode, value, reason)) };
        request.Headers.Add("Idempotency-Key", key ?? Guid.NewGuid().ToString("N"));
        return client.SendAsync(request);
    }

    [Fact]
    public async Task Amount_then_percent_replaces_instead_of_stacking_and_zero_removes_with_audit()
    {
        var before = (await Read())!;
        (await Set(_h.OrderCashier, _h.Session, before, 10000)).EnsureSuccessStatusCode();
        var first = (await Read())!; first.Total.Should().Be(90000); first.Discount.Should().Be(10000);
        (await Set(_h.OrderCashier, _h.Session, first, 20, "PERCENT")).EnsureSuccessStatusCode();
        var second = (await Read())!; second.Total.Should().Be(80000); second.Revision.Should().Be(2);
        (await Set(_h.OrderCashier, _h.Session, second, 0)).EnsureSuccessStatusCode();
        var removed = (await Read())!; removed.Total.Should().Be(100000); removed.Revision.Should().Be(3);
        using var scope = _h.Ordering.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
        (await db.SessionDiscountEvents.CountAsync()).Should().Be(3);
        (await db.Orders.Include(x => x.Items).ToListAsync()).Sum(x => x.DiscountAmount).Should().Be(0);
        (await _h.BillAsync()).Quote.Total.Should().Be(100000);
    }

    [Fact]
    public async Task Lost_response_retry_returns_one_receipt_and_changed_body_is_conflict()
    {
        var before = (await Read())!;
        (await Set(_h.OrderCashier, _h.Session, before, 12345, key: "same-discount-key")).EnsureSuccessStatusCode();
        var retry = await Set(_h.OrderCashier, _h.Session, before, 12345, key: "same-discount-key");
        retry.EnsureSuccessStatusCode(); (await retry.Content.ReadFromJsonAsync<SessionDiscountView>())!.Replay.Should().BeTrue();
        await Set(_h.OrderCashier, _h.Session, before, 12346, key: "same-discount-key").ShouldBe(409, "discount.key_conflict");
        (await Read())!.Revision.Should().Be(1);
        (await _h.BillAsync()).Quote.Total.Should().Be(87655);
    }

    [Theory]
    [InlineData(-1, "AMOUNT", 400)]
    [InlineData(1.5, "AMOUNT", 400)]
    [InlineData(100000, "AMOUNT", 409)]
    [InlineData(100, "PERCENT", 409)]
    [InlineData(101, "PERCENT", 409)]
    [InlineData(1.001, "PERCENT", 400)]
    [InlineData(10, "INVALID", 400)]
    public async Task Invalid_discount_does_not_change_quote(decimal value, string mode, int status)
    {
        var before = (await Read())!;
        await Set(_h.OrderCashier, _h.Session, before, value, mode).ShouldBe(status);
        (await Read())!.Fingerprint.Should().Be(before.Fingerprint);
        (await Read())!.Revision.Should().Be(0);
    }

    [Fact]
    public async Task Reason_stale_quote_and_stale_revision_are_rejected()
    {
        var before = (await Read())!;
        await Set(_h.OrderCashier, _h.Session, before, 1, reason: " ").ShouldBe(400);
        (await Set(_h.OrderCashier, _h.Session, before, 0)).EnsureSuccessStatusCode();
        await Set(_h.OrderCashier, _h.Session, before, 1).ShouldBe(409, "discount.stale");
        var current = (await Read())!; await _h.RoundAsync();
        await Set(_h.OrderCashier, _h.Session, current, 1000).ShouldBe(409, "discount.stale");
    }

    [Fact]
    public async Task Promotion_or_unexplained_existing_discount_is_not_overwritten()
    {
        using (var scope = _h.Ordering.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
            var order = await db.Orders.Include(x => x.Items).FirstAsync();
            order.ApplyPromotion("LOYALTY", 1000); await db.SaveChangesAsync();
        }
        var view = (await Read())!; view.CanEdit.Should().BeFalse(); view.ReasonCode.Should().Be("discount.promotion_exists");
        await Set(_h.OrderCashier, _h.Session, view, 2000).ShouldBe(409, "discount.promotion_exists");
        (await _h.BillAsync()).Quote.Discount.Should().Be(1000);
    }

    [Fact]
    public async Task Payment_freeze_or_paid_blocks_discount_but_old_receipt_can_replay()
    {
        var before = (await Read())!;
        (await Set(_h.OrderCashier, _h.Session, before, 10000, key: "before-paid-key")).EnsureSuccessStatusCode();
        var current = (await Read())!;
        var paid = await _h.CashAsync(await _h.BillAsync()); paid.Quote.Total.Should().Be(90000);
        await Set(_h.OrderCashier, _h.Session, current, 20000).ShouldBe(409, "discount.locked");
        (await Set(_h.OrderCashier, _h.Session, before, 10000, key: "before-paid-key")).EnsureSuccessStatusCode();
        (await _h.BillAsync()).Payment!.Amount.Should().Be(90000);
    }

    [Fact]
    public async Task Discount_between_quote_and_freeze_refuses_stale_cash()
    {
        var bill = await _h.BillAsync(); var before = (await Read())!;
        _h.BeforeFreeze = async () => (await Set(_h.OrderCashier, _h.Session, before, 10000)).EnsureSuccessStatusCode();
        await _h.CollectAsync(bill, "cash").ShouldBe(409);
        (await _h.BillAsync()).Quote.Total.Should().Be(90000);
    }

    [Fact]
    public async Task Added_or_cancelled_round_requires_explicit_discount_review_before_cash()
    {
        var before = (await Read())!;
        (await Set(_h.OrderCashier, _h.Session, before, 20, "PERCENT")).EnsureSuccessStatusCode();
        var newOrder = await _h.RoundAsync();
        var changed = (await Read())!; changed.ReviewRequired.Should().BeTrue();
        (await _h.BillAsync()).Quote.ManualDiscountReviewRequired.Should().BeTrue();
        await _h.CollectAsync(await _h.BillAsync(), "cash").ShouldBe(409);
        (await Set(_h.OrderCashier, _h.Session, changed, 20, "PERCENT")).EnsureSuccessStatusCode();
        (await Read())!.ReviewRequired.Should().BeFalse(); (await _h.BillAsync()).Quote.Total.Should().Be(120000);
        (await _h.BillAsync()).Quote.ManualDiscountReviewRequired.Should().BeFalse();
        (await _h.OrderCashier.PostAsJsonAsync($"/api/v1/orders/{newOrder}/cancel", new { reason = "Test cancellation" })).EnsureSuccessStatusCode();
        var cancelled = (await Read())!; cancelled.ReviewRequired.Should().BeTrue();
        await _h.CollectAsync(await _h.BillAsync(), "cash").ShouldBe(409);
        (await Set(_h.OrderCashier, _h.Session, cancelled, 20, "PERCENT")).EnsureSuccessStatusCode();
        (await _h.CashAsync(await _h.BillAsync())).Quote.Total.Should().Be(80000);
    }

    [Fact]
    public async Task Disabling_discount_mutations_does_not_disable_the_payment_review_guard()
    {
        var before = (await Read())!;
        (await Set(_h.OrderCashier, _h.Session, before, 10000)).EnsureSuccessStatusCode(); await _h.RoundAsync();
        _h.Ordering.Services.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>()["SessionDiscount:Enabled"] = "false";
        (await _h.OrderCashier.GetAsync(Path)).StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        await _h.CollectAsync(await _h.BillAsync(), "cash").ShouldBe(409);
    }

    [Fact]
    public async Task Pending_qr_fence_blocks_discount_without_changing_the_attempt()
    {
        var before = (await Read())!;
        (await _h.CollectAsync(await _h.BillAsync(), "qr")).EnsureSuccessStatusCode();
        var pending = await _h.BillAsync(); pending.Payment!.Status.Should().Be("PENDING");
        (await Read())!.CanEdit.Should().BeFalse();
        await Set(_h.OrderCashier, _h.Session, before, 10000).ShouldBe(409, "discount.locked");
        (await _h.BillAsync()).Payment!.Id.Should().Be(pending.Payment.Id);
    }

    [Theory]
    [InlineData("partial")]
    [InlineData("guest")]
    public async Task Legacy_partial_payment_and_pay_first_orders_are_not_discounted(string scenario)
    {
        using (var scope = _h.Ordering.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
            var order = await db.Orders.FirstAsync();
            if (scenario == "partial") db.Entry(order).Property(x => x.PaymentStatus).CurrentValue = PaymentStatus.PartiallyPaid;
            else db.Entry(order).Property(x => x.CashierId).CurrentValue = null;
            await db.SaveChangesAsync();
        }
        var current = (await Read())!; current.CanEdit.Should().BeFalse();
        await Set(_h.OrderCashier, _h.Session, current, 10000).ShouldBe(409, "discount.order_locked");
    }

    [Fact]
    public async Task Role_branch_and_current_identity_are_revalidated()
    {
        using var barista = _h.Ordering.As(AicosRoles.Barista, _h.Branch);
        (await barista.GetAsync(Path)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        using var other = _h.Ordering.As(AicosRoles.Cashier, Guid.NewGuid());
        (await other.GetAsync(Path)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var before = (await Read())!; _actor.Allowed = false;
        await Set(_h.OrderCashier, _h.Session, before, 10000).ShouldBe(403, "discount.actor_changed");
    }

    [Fact]
    public async Task Default_feature_off_does_not_access_the_new_table()
    {
        using var h = new SessionBillHarness(); await h.InitializeAsync(); await h.RoundAsync();
        (await h.OrderCashier.GetAsync($"/api/v1/orders/qr/sessions/{h.Session}/manual-discount"))
            .StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        JsonSerializer.Serialize((await h.BillAsync()).Quote).Should().NotContain("ManualDiscountReviewRequired");
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(1000000000000000, 1000000000000000)]
    public void Largest_remainder_is_deterministic_and_conserves_whole_vnd(decimal price, decimal amount)
    {
        var orders = Enumerable.Range(0, 3).Select(_ =>
        {
            var order = new Order(Guid.NewGuid(), Guid.NewGuid(), OrderType.DineIn, DateTime.UtcNow);
            order.AddItem(Guid.NewGuid(), "Test", price, 1, null); return order;
        }).ToArray();
        var first = SessionDiscountCalculation.Allocate(orders, "AMOUNT", amount);
        var reversed = SessionDiscountCalculation.Allocate(orders.Reverse().ToArray(), "AMOUNT", amount);
        first.Should().Equal(reversed); first.Sum(x => x.After).Should().Be(amount);
        first.All(x => x.After >= 0 && x.After <= price).Should().BeTrue();
    }
}
