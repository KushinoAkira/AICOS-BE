using System.Net;
using System.Net.Http.Json;
using AICOS.BuildingBlocks.Security;
using AICOS.Contracts.Orders;
using AICOS.KitchenOperations.Api.Domain.Enums;
using AICOS.KitchenOperations.Api.Persistence;
using AICOS.Ordering.Api.Domain.Enums;
using AICOS.Ordering.Api.Features.OrderCancellation;
using AICOS.Ordering.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;

namespace AICOS.Services.IntegrationTests;

// HTTP TestServer + EF InMemory; Identity, Menu, JWT and service transports are explicit doubles.
public sealed class SafeOrderCancellationTests : IAsyncLifetime
{
    private readonly SessionBillHarness _h = new() { EnableSafeCancellation = true, DiscountActor = new DiscountActorDouble() };
    private Guid _order;
    public async Task InitializeAsync() { await _h.InitializeAsync(); _order = await _h.RoundAsync(); await _h.RoundAsync(); }
    public Task DisposeAsync() { _h.Dispose(); return Task.CompletedTask; }
    public static Task<OrderCancellationView?> Read(SessionBillHarness h, Guid order) =>
        h.OrderCashier.GetFromJsonAsync<OrderCancellationView>($"/api/v1/orders/{order}/cancellation");
    public static Task<HttpResponseMessage> Cancel(SessionBillHarness h, Guid order, OrderCancellationView view, string key = "cancel-request-key", string reason = "Customer changed their mind")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/orders/{order}/cancellation")
        { Content = JsonContent.Create(new RequestOrderCancellation(h.Session, view.Fingerprint, reason)) };
        request.Headers.Add("Idempotency-Key", key); return h.OrderCashier.SendAsync(request);
    }
    [Fact]
    public async Task Confirmed_cancellation_voids_only_one_round_and_replay_is_idempotent()
    {
        var before = (await Read(_h, _order))!;
        var response = await Cancel(_h, _order, before); response.EnsureSuccessStatusCode();
        (await response.Content.ReadFromJsonAsync<CancellationOperationView>())!.State.Should().Be("COMPLETED");
        (await Cancel(_h, _order, before)).EnsureSuccessStatusCode();
        await Cancel(_h, _order, before, reason: "Different reason").ShouldBe(409, "order.cancel_key_conflict");
        using var os = _h.Ordering.Services.CreateScope(); var orders = os.ServiceProvider.GetRequiredService<OrderingDbContext>();
        (await orders.Orders.SingleAsync(x => x.Id == _order)).Status.Should().Be(OrderStatus.Cancelled);
        (await orders.Cancellations.CountAsync()).Should().Be(1);
        using var ks = _h.Kitchen.Services.CreateScope(); var kitchen = ks.ServiceProvider.GetRequiredService<KitchenOperationsDbContext>();
        (await kitchen.Tickets.SingleAsync(x => x.OrderId == _order)).Status.Should().Be(KitchenTicketStatus.Cancelled);
        (await kitchen.CancellationReceipts.CountAsync()).Should().Be(1);
        (await _h.BillAsync()).Quote.Total.Should().Be(50000);
    }
    [Fact]
    public async Task Offline_is_pending_blocks_finance_and_recovers_by_same_operation()
    {
        _h.KitchenOffline = true; var before = (await Read(_h, _order))!;
        var response = await Cancel(_h, _order, before); response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var pending = (await response.Content.ReadFromJsonAsync<CancellationOperationView>())!;
        var bill = await _h.BillAsync(); bill.Quote.OrderCancellationPending.Should().BeTrue();
        await _h.CollectAsync(bill, "cash").ShouldBe(409);
        await _h.AddRoundAsync("cannot-add-while-cancelling").ShouldBe(409, "order.cancel_pending");
        using (var scope = _h.Ordering.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
            var session = await db.TableSessions.SingleAsync(x => x.Id == _h.Session);
            session.Touch(DateTime.UtcNow); await db.SaveChangesAsync();
        }
        using (var scope = _h.Ordering.Services.CreateScope())
            (await scope.ServiceProvider.GetRequiredService<OrderingDbContext>().Orders.SingleAsync(x => x.Id == _order)).IsVoided.Should().BeFalse();
        _h.KitchenOffline = false;
        var retry = await _h.OrderCashier.PostAsync($"/api/v1/orders/{_order}/cancellation/{pending.Id}/retry", null);
        retry.EnsureSuccessStatusCode(); (await retry.Content.ReadFromJsonAsync<CancellationOperationView>())!.State.Should().Be("COMPLETED");
        (await _h.BillAsync()).Quote.OrderCancellationPending.Should().BeFalse();
    }
    [Fact]
    public async Task Bar_started_is_rejected_without_voiding_order_or_ticket()
    {
        using (var scope = _h.Kitchen.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<KitchenOperationsDbContext>();
            var ticket = await db.Tickets.SingleAsync(x => x.OrderId == _order); ticket.StartBrewing(DateTime.UtcNow); await db.SaveChangesAsync();
        }
        var response = await Cancel(_h, _order, (await Read(_h, _order))!); response.EnsureSuccessStatusCode();
        (await response.Content.ReadFromJsonAsync<CancellationOperationView>())!.State.Should().Be("REJECTED");
        (await _h.BillAsync()).Quote.Total.Should().Be(100000);
    }
    [Fact]
    public async Task Stale_reason_legacy_and_cross_branch_cannot_bypass_protocol()
    {
        var before = (await Read(_h, _order))!;
        await Cancel(_h, _order, before with { Fingerprint = new string('A', 64) }).ShouldBe(409, "order.cancel_stale");
        await Cancel(_h, _order, before, reason: " ").ShouldBe(400);
        await _h.OrderCashier.PostAsJsonAsync($"/api/v1/orders/{_order}/cancel", new { reason = "legacy" }).ShouldBe(409, "order.cancel_protocol_required");
        using var stranger = _h.Ordering.As(AicosRoles.Cashier, Guid.NewGuid());
        (await stranger.GetAsync($"/api/v1/orders/{_order}/cancellation")).StatusCode.Should().BeOneOf(HttpStatusCode.Forbidden, HttpStatusCode.NotFound);
    }
    [Fact]
    public async Task Paid_order_cannot_be_cancelled()
    {
        await _h.CashAsync(await _h.BillAsync());
        var view = (await Read(_h, _order))!; view.CanRequest.Should().BeFalse();
        await Cancel(_h, _order, view).ShouldBe(409);
    }
    [Fact]
    public async Task Pausing_capture_keeps_pending_status_retry_and_guards_available()
    {
        _h.KitchenOffline = true;
        (await Cancel(_h, _order, (await Read(_h, _order))!)).StatusCode.Should().Be(HttpStatusCode.Accepted);
        _h.Ordering.Services.GetRequiredService<IConfiguration>()["OrderCancellation:Enabled"] = "false";
        var pending = (await Read(_h, _order))!;
        await Cancel(_h, _order, pending, "paused-new-request").ShouldBe(503);
        _h.KitchenOffline = false;
        var retry = await _h.OrderCashier.PostAsync($"/api/v1/orders/{_order}/cancellation/{pending.Operation!.Id}/retry", null);
        retry.EnsureSuccessStatusCode();
        (await retry.Content.ReadFromJsonAsync<CancellationOperationView>())!.State.Should().Be("COMPLETED");
    }
    [Fact]
    public async Task Live_actor_revocation_and_barista_only_role_are_refused()
    {
        using var server = _h.Ordering.As(AicosRoles.Barista, _h.Branch);
        await server.GetAsync($"/api/v1/orders/{_order}/cancellation").ShouldBe(403);
        ((DiscountActorDouble)_h.DiscountActor!).Allowed = false;
        await _h.OrderCashier.GetAsync($"/api/v1/orders/{_order}/cancellation").ShouldBe(403);
    }
    [Fact]
    public async Task Promotion_requires_separate_recovery_contract_and_cup_mode_stays_blocked()
    {
        using var scope = _h.Ordering.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
        var order = await db.Orders.SingleAsync(x => x.Id == _order);
        order.ApplyPromotion("TEST", 1000); await db.SaveChangesAsync();
        var view = (await Read(_h, _order))!; view.ReasonCode.Should().Be("order.cancel_promotion_review_required");
        await Cancel(_h, _order, view).ShouldBe(409);
        db.Entry(order).Property("FulfillmentMode").CurrentValue = "CupV1"; await db.SaveChangesAsync();
        (await Read(_h, _order))!.ReasonCode.Should().Be("order.cup_cancellation_required");
    }
    [Fact]
    public async Task Machine_endpoint_requires_separate_key_and_abort_fences_late_reserve()
    {
        var view = (await Read(_h, _order))!;
        var body = new CancellationReservation(Guid.NewGuid(), _h.Tenant, _h.Branch, _order, _h.CashierId, view.OrderCode, "A01", view.Fingerprint);
        using var client = _h.Kitchen.CreateClient();
        await client.PostAsJsonAsync("/internal/order-cancellations/reserve", body).ShouldBe(401);
        client.DefaultRequestHeaders.Add(OrderCancellationMachineAuth.Header, _h.MachineKey);
        await client.PostAsJsonAsync("/internal/order-cancellations/reserve", body).ShouldBe(401);
        client.DefaultRequestHeaders.Remove(OrderCancellationMachineAuth.Header); client.DefaultRequestHeaders.Add(OrderCancellationMachineAuth.Header, _h.CancellationKey);
        (await client.PostAsJsonAsync("/internal/order-cancellations/abort", body)).EnsureSuccessStatusCode();
        var late = await client.PostAsJsonAsync("/internal/order-cancellations/reserve", body);
        (await late.Content.ReadFromJsonAsync<CancellationAck>())!.State.Should().Be("ABORTED");
    }
}
