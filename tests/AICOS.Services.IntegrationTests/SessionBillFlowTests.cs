using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AICOS.BuildingBlocks.Security;
using AICOS.Payments.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
namespace AICOS.Services.IntegrationTests;

public sealed class SessionBillFlowTests : IAsyncLifetime
{
    private readonly SessionBillHarness _h = new();
    public Task InitializeAsync() => _h.InitializeAsync();
    public Task DisposeAsync() { _h.Dispose(); return Task.CompletedTask; }
    [Fact]
    public async Task A_session_has_one_bill_for_multiple_orders_and_cash_replay_is_safe()
    {
        await _h.RoundAsync(); await _h.RoundAsync(); var draft = await _h.BillAsync();
        draft.Status.Should().Be("DRAFT"); draft.Quote.Total.Should().Be(100000); draft.Quote.Orders.Should().HaveCount(2);
        using (var s = _h.Payments.Services.CreateScope()) (await s.ServiceProvider.GetRequiredService<PaymentsDbContext>().SessionBills.CountAsync()).Should().Be(0);
        var paid = await _h.CashAsync(draft, "same-cash-key"); paid.Status.Should().Be("PAID"); paid.OrderingAcknowledged.Should().BeTrue();
        (await _h.CashAsync(draft, "same-cash-key")).Payment!.Id.Should().Be(paid.Payment!.Id);
        await _h.CollectAsync(draft, "cash", "another-cash-key").ShouldBe(409);
        using (var s = _h.Payments.Services.CreateScope())
        { var db = s.ServiceProvider.GetRequiredService<PaymentsDbContext>(); (await db.SessionBills.CountAsync()).Should().Be(1); (await db.BillAttempts.CountAsync()).Should().Be(1); (await db.BillJournal.CountAsync(e => e.Kind == "PAID")).Should().Be(1); }
    }
    [Fact]
    public async Task Cash_requires_explicit_receipt_and_enough_money()
    {
        await _h.RoundAsync(); var draft = await _h.BillAsync();
        await _h.CollectAsync(draft, "cash", confirm: false).ShouldBe(400);
        await _h.CollectAsync(draft, "cash", tendered: 1).ShouldBe(400);
        (await _h.BillAsync()).Status.Should().Be("DRAFT");
    }
    [Fact]
    public async Task New_round_between_quote_and_freeze_is_rejected_without_collecting()
    {
        await _h.RoundAsync(); var draft = await _h.BillAsync();
        _h.BeforeFreeze = async () => { (await _h.AddRoundAsync()).EnsureSuccessStatusCode(); };
        await _h.CollectAsync(draft, "cash").ShouldBe(409);
        var current = await _h.BillAsync(); current.Status.Should().Be("DRAFT"); current.Quote.Total.Should().Be(100000);
    }
    [Fact]
    public async Task Paid_before_serving_stays_open_then_closes_idempotently_after_delivery()
    {
        var first = await _h.RoundAsync(); var second = await _h.RoundAsync(); var bill = await _h.CashAsync(await _h.BillAsync());
        var close = $"/api/v1/orders/qr/sessions/{_h.Session}/close";
        await _h.OrderCashier.Post(close, new { }).ShouldBe(409);
        await _h.AddRoundAsync().ShouldBe(409);
        await _h.OrderCashier.Post("/api/v1/orders", new { type = "DineIn", tableId = _h.Table, tableNumber = "A01", items = new[] { new { productId = _h.Product, quantity = 1 } } }).ShouldBe(409);
        await _h.ServeAsync(first); await _h.ServeAsync(second);
        var ready = await _h.OrderCashier.GetFromJsonAsync<JsonElement>($"/api/v1/orders/qr/sessions/{_h.Session}/close-eligibility");
        ready.GetProperty("canClose").GetBoolean().Should().BeTrue();
        await _h.OrderCashier.Post(close, new { }).ShouldBe(204); await _h.OrderCashier.Post(close, new { }).ShouldBe(204);
        (await _h.BillAsync()).Status.Should().Be("CLOSED");
        var invoice = await _h.Cashier.GetFromJsonAsync<JsonElement>($"/api/v1/payments/bills/{bill.Id}/invoice");
        invoice.GetProperty("payment").GetProperty("status").GetString().Should().Be("SUCCEEDED");
    }
    [Fact]
    public async Task Qr_is_pending_until_signed_receipt_and_duplicate_or_late_failure_cannot_regress_paid()
    {
        await _h.RoundAsync(); var draft = await _h.BillAsync();
        var qrResponse = await _h.CollectAsync(draft, "qr", "same-qr-key"); qrResponse.EnsureSuccessStatusCode(); var qr = await qrResponse.Content.ReadFromJsonAsync<AICOS.Payments.Api.Features.SessionBilling.BillView>();
        qr!.Status.Should().Be("UNPAID"); qr.Payment!.Status.Should().Be("PENDING"); qr.ProviderIsTest.Should().BeTrue();
        await _h.CollectAsync(draft, "cash").ShouldBe(409);
        await _h.ReceiptAsync(qr, "bad-sig", signature: "wrong").ShouldBe(401);
        (await _h.BillAsync()).Status.Should().Be("UNPAID");
        _h.Payments.Clock.Now = _h.Payments.Clock.Now.AddDays(2);
        (await _h.BillAsync()).Payment!.Status.Should().Be("PENDING"); // GET never expires or polls a provider.
        await _h.ReceiptAsync(qr, "receipt-1").ShouldBe(200); await _h.ReceiptAsync(qr, "receipt-1").ShouldBe(200);
        await _h.ReceiptAsync(qr, "failure-after-paid", "failed").ShouldBe(200);
        var paid = await _h.BillAsync(); paid.Status.Should().Be("PAID"); paid.Payment!.Status.Should().Be("SUCCEEDED");
    }
    [Fact]
    public async Task Signed_amount_mismatch_is_audited_and_never_counts_as_payment()
    {
        await _h.RoundAsync(); var response = await _h.CollectAsync(await _h.BillAsync(), "qr"); response.EnsureSuccessStatusCode();
        var qr = (await response.Content.ReadFromJsonAsync<AICOS.Payments.Api.Features.SessionBilling.BillView>())!;
        await _h.ReceiptAsync(qr, "wrong-amount", amount: 1).ShouldBe(200);
        var current = await _h.BillAsync(); current.NeedsReconciliation.Should().BeTrue(); current.Status.Should().Be("UNPAID");
        using var scope = _h.Payments.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<PaymentsDbContext>().BillJournal.CountAsync(e => e.Kind == "AMOUNT_MISMATCH")).Should().Be(1);
    }
    [Fact]
    public async Task Failed_qr_then_cash_and_late_qr_money_are_audited_and_block_closure()
    {
        await _h.RoundAsync(); var draft = await _h.BillAsync(); var response = await _h.CollectAsync(draft, "qr"); response.EnsureSuccessStatusCode();
        var qr = (await response.Content.ReadFromJsonAsync<AICOS.Payments.Api.Features.SessionBilling.BillView>())!;
        await _h.ReceiptAsync(qr, "declined", "failed").ShouldBe(200);
        await _h.CashAsync(draft);
        await _h.ReceiptAsync(qr, "late-money").ShouldBe(200);
        var review = await _h.BillAsync(); review.NeedsReconciliation.Should().BeTrue();
        await _h.Cashier.GetAsync($"/api/v1/payments/bills/{draft.Id}/invoice").ShouldBe(409);
        await _h.OrderCashier.Post($"/api/v1/orders/qr/sessions/{_h.Session}/close", new { }).ShouldBe(409);
    }
    [Fact]
    public async Task Lost_ack_recovers_from_durable_outbox_without_collecting_or_projecting_twice()
    {
        await _h.RoundAsync(); var draft = await _h.BillAsync(); _h.LosePaidAck = true;
        var paid = await _h.CashAsync(draft); paid.Status.Should().Be("PAID"); paid.OrderingAcknowledged.Should().BeFalse();
        await _h.DispatchAgainAsync(); (await _h.BillAsync()).OrderingAcknowledged.Should().BeTrue();
        using var scope = _h.Payments.Services.CreateScope(); (await scope.ServiceProvider.GetRequiredService<PaymentsDbContext>().BillAttempts.CountAsync()).Should().Be(1);
    }
    [Fact]
    public async Task Kitchen_offline_keeps_payment_and_outbox_then_receives_proof_after_recovery()
    {
        await _h.RoundAsync(); _h.KitchenOffline = true; var bill = await _h.CashAsync(await _h.BillAsync()); bill.OrderingAcknowledged.Should().BeFalse();
        _h.KitchenOffline = false; await _h.DispatchAgainAsync(); (await _h.BillAsync()).OrderingAcknowledged.Should().BeTrue();
    }
    [Fact]
    public async Task Frozen_unpaid_qr_blocks_cancel_and_claimed_key_cannot_change_request()
    {
        var order = await _h.RoundAsync(); var draft = await _h.BillAsync(); (await _h.CollectAsync(draft, "qr", "bound-request-key")).EnsureSuccessStatusCode();
        await _h.OrderCashier.Post($"/api/v1/orders/{order}/cancel", new { reason = "must not change frozen total" }).ShouldBe(409);
        await _h.CollectAsync(draft, "cash", "bound-request-key").ShouldBe(409);
        (await _h.BillAsync()).Quote.Total.Should().Be(50000);
    }
    [Fact]
    public async Task Barista_cannot_collect_or_get_invoice_and_internal_api_rejects_user_jwt()
    {
        await _h.RoundAsync(); var draft = await _h.BillAsync(); using var barista = _h.Payments.As(AicosRoles.Barista, _h.Branch);
        await _h.CollectAsync(draft, "cash", caller: barista).ShouldBe(403);
        await _h.Cashier.Post("/internal/session-billing/bills/" + draft.Id + "/seal", new { }).ShouldBe(401);
        using var caller = _h.Ordering.CreateClient();
        await caller.Post($"/internal/session-billing/sessions/{_h.Session}/freeze", new { }).ShouldBe(401);
    }
    private sealed class TimeoutGateway : AICOS.Payments.Api.Infrastructure.IPaymentGateway
    {
        public string Code => "Mock";
        public int Calls;
        public Task<AICOS.Payments.Api.Infrastructure.GatewayCheckout> CreatePaymentAsync(AICOS.Payments.Api.Infrastructure.GatewayPaymentRequest r, CancellationToken ct)
        { Calls++; throw new HttpRequestException("Controlled lost provider response"); }
        public Task<AICOS.Payments.Api.Infrastructure.GatewayStatus> QueryAsync(AICOS.Payments.Api.Domain.Payment p, CancellationToken ct) => throw new NotSupportedException();
        public Task<AICOS.Payments.Api.Infrastructure.GatewayRefundResult> RefundAsync(AICOS.Payments.Api.Domain.Payment p, decimal amount, string reason, CancellationToken ct) => throw new NotSupportedException();
    }
    [Fact]
    public async Task Provider_timeout_keeps_a_durable_pending_reference_and_never_creates_again_on_retry()
    {
        using var h = new SessionBillHarness(); var gateway = new TimeoutGateway(); h.TestGateway = gateway; await h.InitializeAsync();
        await h.RoundAsync(); var draft = await h.BillAsync();
        await h.CollectAsync(draft, "qr", "provider-timeout-key").ShouldBe(503);
        var saved = await h.BillAsync(); saved.Status.Should().Be("UNPAID"); saved.Payment!.Status.Should().Be("PENDING"); saved.Payment.CheckoutUrl.Should().BeNull();
        (await h.CollectAsync(draft, "qr", "provider-timeout-key")).EnsureSuccessStatusCode();
        gateway.Calls.Should().Be(1); await h.CollectAsync(draft, "cash").ShouldBe(409);
    }

}
