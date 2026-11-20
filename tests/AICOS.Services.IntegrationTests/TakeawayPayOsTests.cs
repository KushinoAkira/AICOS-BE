using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AICOS.Payments.Api;
using AICOS.Payments.Api.Infrastructure;
using AICOS.Payments.Api.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace AICOS.Services.IntegrationTests;

public sealed class TakeawayPayOsTests : PaymentTestBase
{
    private readonly FakePayOsApi _api;
    public TakeawayPayOsTests() : this(new FakePayOsApi()) { }
    private TakeawayPayOsTests(FakePayOsApi api) : base(s =>
    {
        s.Configure<PayOsOptions>(o => { o.StaffPaymentsEnabled = true; o.ReceivingAccountNumber = PayOsBillFixture.Account;
            o.MerchantTenantId = ServiceFactory<PaymentsService, PaymentsDbContext>.DefaultTenantId;
            o.ReturnUrl = "https://aicos.example.test/api/v1/payments/payos/return"; o.ResultPageUrl = "https://aicos.example.test/result"; });
        s.AddHttpClient(PayOsGateway.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => api);
    })
    {
        _api = api;
        api.Answer = call => call.Method == HttpMethod.Post
            ? PayOsBillFixture.Signed(new { orderCode = call.Body!.Value.GetProperty("orderCode").GetInt64(),
                amount = call.Body.Value.GetProperty("amount").GetInt64(), currency = "VND", accountNumber = PayOsBillFixture.Account,
                checkoutUrl = "https://pay.payos.vn/web/takeaway", status = "PENDING" })
            : new { code = "00", data = new { orderCode = long.Parse(call.Path.Split('/').Last()), amount = 100000, amountPaid = 100000, status = "PAID", id = "query-receipt" } };
        Super.PostOk(Providers, new { code = "PayOS", displayName = "Isolated payOS", mode = "Sandbox", merchantId = "isolated-client",
            apiKey = "isolated-api-key", secretKey = PayOsBillFixture.Key,
            callbackUrl = "https://aicos.example.test/api/v1/payments/payos/webhook" }).GetAwaiter().GetResult();
        SetProviderEnabledAsync("PayOS", true).GetAwaiter().GetResult();
        Admin.Put(Merchants, new { providerCode = "PayOS", isEnabled = true }).ShouldBe(201).GetAwaiter().GetResult();
    }
    [Fact]
    public async Task Takeaway_payos_uses_existing_order_ledger_with_one_intent_and_authoritative_query()
    {
        var order = Guid.NewGuid(); Orders.Set(order, B1, 100000);
        var path = $"/api/v1/payments/staff/takeaway/{order}/payos";
        Cashier1.DefaultRequestHeaders.Add("Idempotency-Key", "takeaway-payos-stable-key");
        var first = await Cashier1.PostOk(path, new { branchId = B1, amount = 100000 });
        first.GetProperty("providerIsTest").GetBoolean().Should().BeFalse(); first.GetProperty("state").GetString().Should().Be("Pending");
        var payment = first.GetProperty("attempt").GetProperty("id").GetGuid();
        (await Cashier1.PostOk(path, new { branchId = B1, amount = 100000 })).GetProperty("attempt").GetProperty("id").GetGuid().Should().Be(payment);
        _api.Calls.Count(x => x.Method == HttpMethod.Post).Should().Be(1);
        await Cashier1.Post(Tx + "/cash", new { branchId = B1, orderId = order, amount = 100000 }).ShouldBe(409);
        var confirmed = await Cashier1.PostOk(path + $"/{payment}/query?branchId={B1}", new { });
        confirmed.GetProperty("state").GetString().Should().Be("Confirmed");
    }
    [Fact]
    public async Task Wrong_branch_or_amount_never_calls_provider()
    {
        var order = Guid.NewGuid(); Orders.Set(order, B1, 100000); var path = $"/api/v1/payments/staff/takeaway/{order}/payos";
        Cashier1.DefaultRequestHeaders.Add("Idempotency-Key", "takeaway-payos-invalid-key");
        await Cashier1.Post(path, new { branchId = B1, amount = 1 }).ShouldBe(400);
        await Cashier2.GetAsync(path + $"?branchId={B1}").ShouldBe(403); _api.Calls.Should().BeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Staff_browser_return_reads_only_verified_state_even_with_feature_off(bool confirmed)
    {
        var order = Guid.NewGuid(); Orders.Set(order, B1, 100000);
        var path = $"/api/v1/payments/staff/takeaway/{order}/payos";
        Cashier1.DefaultRequestHeaders.Add("Idempotency-Key", "takeaway-payos-browser-key");
        var pending = await Cashier1.PostOk(path, new { branchId = B1, amount = 100000 });
        var payment = pending.GetProperty("attempt");
        if (confirmed) await Cashier1.PostOk(path + $"/{payment.GetProperty("id").GetGuid()}/query?branchId={B1}", new { });
        var calls = _api.Calls.Count;
        F.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<PayOsOptions>>().Value.StaffPaymentsEnabled = false;
        using var browser = F.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var response = await browser.GetAsync("/api/v1/payments/payos/return?orderCode="
            + PayOs.OrderCodeOf(payment.GetProperty("transactionRef").GetString()!) + "&status=PAID&success=true");
        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.GetValues("X-Aicos-Return-Mode").Single().Should().Be("read-only");
        var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(response.Headers.Location!.Query);
        query["status"].ToString().Should().Be(confirmed ? "paid" : "pending");
        _api.Calls.Count.Should().Be(calls);
        (await Cashier1.GetAsync(path + $"?branchId={B1}")).EnsureSuccessStatusCode();
    }
}
