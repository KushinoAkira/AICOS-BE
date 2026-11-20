using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AICOS.BuildingBlocks.Security;
using AICOS.Contracts.Billing;
using AICOS.Payments.Api.Domain;
using AICOS.Payments.Api.Features.SessionBilling;
using AICOS.Payments.Api.Infrastructure;
using AICOS.Payments.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AICOS.Services.IntegrationTests;

// Real HTTP handlers/ledger; provider HTTP, Identity/Menu/transport are explicit doubles.
public sealed class PayOsBillFixture : IDisposable
{
    public const string Account = "0123456789", Key = "isolated-payos-checksum-not-a-real-key";
    public readonly SessionBillHarness H = new() { EnableCashierShifts = true, RequireCashierShift = true };
    public readonly FakePayOsApi Api = new();
    public bool LoseCreateAck, BadCreateSignature;
    public string QueryState = "PENDING";
    public long? ReceivedAmount;
    public PayOsOptions Options => H.Payments.Services.GetRequiredService<IOptions<PayOsOptions>>().Value;

    public async Task InitializeAsync(string? connection = null)
    {
        H.ConfigurePayments = services =>
        {
            services.Configure<PayOsOptions>(o => { o.StaffPaymentsEnabled = true; o.MerchantTenantId = H.Tenant;
                o.ReceivingAccountNumber = Account; o.ReturnUrl = "https://aicos.example.test/api/v1/payments/payos/return";
                o.ResultPageUrl = "https://aicos.example.test/payment-result"; });
            services.AddHttpClient(PayOsGateway.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => Api);
        };
        Api.Answer = call =>
        {
            if (call.Method == HttpMethod.Post)
            {
                if (LoseCreateAck) throw new HttpRequestException("Controlled lost ACK after provider intent creation");
                var code = call.Body!.Value.GetProperty("orderCode").GetInt64();
                return Signed(new { orderCode = code, amount = call.Body.Value.GetProperty("amount").GetInt64(), currency = "VND",
                    accountNumber = Account, checkoutUrl = "https://pay.payos.vn/web/link" + code, status = "PENDING", paymentLinkId = "link" + code }, BadCreateSignature);
            }
            return new { code = "00", data = new { orderCode = long.Parse(call.Path.Split('/').Last()), amount = 100000,
                amountPaid = ReceivedAmount ?? (QueryState == "PAID" ? 100000 : 0), status = QueryState, id = "link-query-proof" } };
        };
        await H.InitializeAsync(connection);
        using var scope = H.Payments.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        var protector = scope.ServiceProvider.GetRequiredService<SecretProtector>();
        var provider = await db.PaymentProviders.SingleOrDefaultAsync(x => x.Code == "PayOS");
        if (provider is null) { provider = new PaymentProvider("PayOS", "Isolated payOS transport", ProviderMode.Sandbox); db.PaymentProviders.Add(provider); }
        provider.Update("Isolated payOS transport", ProviderMode.Sandbox, "isolated-client", "https://aicos.example.test/api/v1/payments/payos/webhook");
        provider.SetCredentials(protector.Protect("isolated-api-key"), protector.Protect(Key)); provider.SetEnabled(true); await db.SaveChangesAsync();
        using var admin = H.Payments.As(AicosRoles.BrandAdmin);
        (await admin.PutAsJsonAsync("/api/v1/payments/merchant-settings", new { providerCode = "PayOS", isEnabled = true })).EnsureSuccessStatusCode();
        await H.RoundAsync(); await H.RoundAsync();
    }
    public static object Signed(object data, bool bad = false)
    {
        var json = JsonSerializer.SerializeToElement(data);
        var canonical = string.Join("&", json.EnumerateObject().OrderBy(x => x.Name, StringComparer.Ordinal)
            .Select(x => x.Name + "=" + (x.Value.ValueKind == JsonValueKind.String ? x.Value.GetString() : x.Value.GetRawText())));
        return new { code = "00", desc = "success", data, signature = bad ? new string('0', 64) : PayOsSignature.Hmac(Key, canonical) };
    }
    public object Receipt(BillView bill, long? amount = null, string account = Account, bool bad = false, string reference = "bank-ref") =>
        Signed(new { orderCode = PayOs.OrderCodeOf(bill.Payment!.TransactionRef), amount = amount ?? (long)bill.Quote.Total,
            accountNumber = account, currency = "VND", reference, description = "AICOS", transactionDateTime = "2026-11-20 00:00:00",
            paymentLinkId = "link", code = "00", desc = "success" }, bad);
    public async Task<HttpResponseMessage> CreateAsync(BillView bill, string key = "payos-stable-key")
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/payments/bills/{bill.Id}/payments/qr")
        { Content = JsonContent.Create(new CollectBillRequest(H.Session, bill.Fingerprint, ProviderCode: "PayOS")) };
        request.Headers.Add("Idempotency-Key", key);
        return await H.Cashier.SendAsync(request);
    }
    public Task<HttpResponseMessage> HookAsync(object payload) => H.Payments.CreateClient().PostAsJsonAsync("/api/v1/payments/payos/webhook", payload);
    public Task<HttpResponseMessage> QueryAsync(BillView bill, HttpClient? user = null) => (user ?? H.Cashier)
        .PostAsJsonAsync($"/api/v1/payments/bills/{bill.Id}/payments/{bill.Payment!.Id}/query", new { });
    public void Dispose() => H.Dispose();
}

public sealed class PayOsBillTests : IAsyncLifetime
{
    private readonly PayOsBillFixture _f = new();
    public Task InitializeAsync() => _f.InitializeAsync();
    public Task DisposeAsync() { _f.Dispose(); return Task.CompletedTask; }
    private async Task<BillView> StartAsync()
    {
        var draft = await _f.H.BillAsync(); draft.QrAvailable.Should().BeTrue(); draft.QrProviderCode.Should().Be("PayOS");
        (await _f.CreateAsync(draft)).EnsureSuccessStatusCode(); return await _f.H.BillAsync();
    }
    [Fact]
    public async Task PayOs_contract_replays_signed_receipt_once_without_order_payment_or_cash_drawer()
    {
        var bill = await StartAsync(); bill.ProviderIsTest.Should().BeFalse(); bill.Payment!.Status.Should().Be("PENDING");
        var code = PayOs.OrderCodeOf(bill.Payment.TransactionRef);
        PayOs.TransactionRefPrefixOf(code).Should().BeNull(); PayOs.BillReferencePrefixOf(code).Should().NotBeNull();
        (await _f.CreateAsync(bill)).EnsureSuccessStatusCode(); _f.Api.Calls.Count(x => x.Method == HttpMethod.Post).Should().Be(1);
        (await _f.HookAsync(_f.Receipt(bill))).EnsureSuccessStatusCode(); (await _f.HookAsync(_f.Receipt(bill))).EnsureSuccessStatusCode();
        var paid = await _f.H.BillAsync(); paid.Status.Should().Be("PAID"); paid.OrderingAcknowledged.Should().BeTrue();
        using var scope = _f.H.Payments.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        (await db.Payments.CountAsync()).Should().Be(0); (await db.BillAttempts.CountAsync()).Should().Be(1);
        (await db.BillJournal.CountAsync(x => x.Kind == "PAID")).Should().Be(1);
        var collection = await db.Collections.SingleAsync(); collection.Method.Should().Be("QR"); collection.ShiftId.Should().BeNull(); collection.CashierId.Should().BeNull(); collection.Amount.Should().Be(100000);
        await _f.H.AddRoundAsync().ShouldBe(409);
        await _f.H.OrderCashier.Post($"/api/v1/orders/qr/sessions/{_f.H.Session}/close", new { }).ShouldBe(409);
    }
    [Theory]
    [InlineData(50000, PayOsBillFixture.Account)]
    [InlineData(100000, "9999999999")]
    public async Task Wrong_amount_or_receiver_requires_reconciliation_without_paid(long amount, string account)
    {
        var bill = await StartAsync(); (await _f.HookAsync(_f.Receipt(bill, amount, account))).EnsureSuccessStatusCode();
        var current = await _f.H.BillAsync(); current.Status.Should().Be("UNPAID"); current.NeedsReconciliation.Should().BeTrue();
        await _f.H.CollectAsync(current, "cash").ShouldBe(409);
    }
    [Fact]
    public async Task Forged_callback_and_public_mock_route_cannot_confirm_bank_receipt()
    {
        var bill = await StartAsync(); await _f.HookAsync(_f.Receipt(bill, bad: true)).ShouldBe(401);
        await _f.H.ReceiptAsync(bill, "mock-cannot-confirm-payos").ShouldBe(404);
        (await _f.H.BillAsync()).Payment!.Status.Should().Be("PENDING");
    }
    [Fact]
    public async Task Lost_create_ack_reopens_same_attempt_then_explicit_query_confirms_once()
    {
        _f.LoseCreateAck = true; var draft = await _f.H.BillAsync(); await _f.CreateAsync(draft).ShouldBe(503);
        var pending = await _f.H.BillAsync(); pending.Payment!.CheckoutUrl.Should().BeNull();
        (await _f.CreateAsync(draft)).EnsureSuccessStatusCode(); _f.Api.Calls.Count(x => x.Method == HttpMethod.Post).Should().Be(1);
        _f.QueryState = "PAID"; (await _f.QueryAsync(pending)).EnsureSuccessStatusCode(); (await _f.QueryAsync(pending)).EnsureSuccessStatusCode();
        (await _f.HookAsync(_f.Receipt(pending))).EnsureSuccessStatusCode();
        (await _f.H.BillAsync()).Status.Should().Be("PAID");
    }
    [Fact]
    public async Task Bad_signed_checkout_never_becomes_a_usable_payment_qr()
    {
        _f.BadCreateSignature = true; var draft = await _f.H.BillAsync(); await _f.CreateAsync(draft).ShouldBe(503);
        (await _f.H.BillAsync()).Payment!.CheckoutUrl.Should().BeNull();
    }
    [Fact]
    public async Task Switch_off_blocks_new_intents_but_does_not_drop_receipts_for_existing_money()
    {
        var bill = await StartAsync(); _f.Options.StaffPaymentsEnabled = false;
        (await _f.HookAsync(_f.Receipt(bill))).EnsureSuccessStatusCode(); (await _f.H.BillAsync()).Status.Should().Be("PAID");
    }
    [Fact]
    public async Task Failed_attempt_or_query_amount_mismatch_cannot_be_replaced_with_cash()
    {
        var bill = await StartAsync(); _f.QueryState = "CANCELLED";
        (await _f.QueryAsync(bill)).EnsureSuccessStatusCode(); var failed = await _f.H.BillAsync(); failed.Payment!.Status.Should().Be("FAILED");
        await _f.H.CollectAsync(failed, "cash").ShouldBe(409, "payment.online_pending");
        _f.QueryState = "PAID"; _f.ReceivedAmount = 120000; (await _f.QueryAsync(failed)).EnsureSuccessStatusCode();
        (await _f.H.BillAsync()).NeedsReconciliation.Should().BeTrue();
    }
    [Fact]
    public async Task Cross_branch_and_barista_queries_are_denied_before_provider_call()
    {
        var bill = await StartAsync(); var count = _f.Api.Calls.Count;
        using var barista = _f.H.Payments.As(AicosRoles.Barista, _f.H.Branch);
        await _f.QueryAsync(bill, barista).ShouldBe(403);
        var foreign = _f.H.Payments.Branches.Add(_f.H.Tenant); using var cashier = _f.H.Payments.As(AicosRoles.Cashier, foreign);
        await _f.QueryAsync(bill, cashier).ShouldBe(403); _f.Api.Calls.Count.Should().Be(count);
    }
    [Fact]
    public async Task Unsigned_browser_return_cannot_confirm_payment_or_query_bank()
    {
        var bill = await StartAsync(); var count = _f.Api.Calls.Count;
        using var browser = _f.H.Payments.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var response = await browser.GetAsync("/api/v1/payments/payos/return?orderCode=" + PayOs.OrderCodeOf(bill.Payment!.TransactionRef) + "&status=PAID&success=true");
        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.Query.Should().Contain("status=pending");
        _f.Api.Calls.Count.Should().Be(count); (await _f.H.BillAsync()).Payment!.Status.Should().Be("PENDING");
    }
    [Theory]
    [InlineData("flag")]
    [InlineData("tenant")]
    [InlineData("host")]
    [InlineData("return")]
    [InlineData("account")]
    public async Task Unapproved_or_incomplete_channel_cannot_freeze_or_call_bank(string missing)
    {
        switch (missing)
        {
            case "flag": _f.Options.StaffPaymentsEnabled = false; break;
            case "tenant": _f.Options.MerchantTenantId = Guid.NewGuid(); break;
            case "host": _f.Options.ApiUrl = "http://internal.invalid"; break;
            case "return": _f.Options.ReturnUrl = "http://localhost:8081/return"; break;
            case "account": _f.Options.ReceivingAccountNumber = null; break;
        }
        var draft = await _f.H.BillAsync(); await _f.CreateAsync(draft).ShouldBe(503); _f.Api.Calls.Should().BeEmpty();
        using var scope = _f.H.Payments.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<PaymentsDbContext>().BillAttempts.CountAsync()).Should().Be(0);
    }
}
