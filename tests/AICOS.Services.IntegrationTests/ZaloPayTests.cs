using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AICOS.Contracts.Events;
using AICOS.Payments.Api.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;

namespace AICOS.Services.IntegrationTests;

/// <summary>Stands in for the ZaloPay API: records each form call and answers what the test says.</summary>
public sealed class FakeZaloPayApi : HttpMessageHandler
{
    public sealed record Call(string Path, Dictionary<string, string> Form);

    public List<Call> Calls { get; } = [];

    public Func<Call, object> Answer { get; set; } = _ => new { return_code = 2, return_message = "not set up" };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var form = QueryHelpers.ParseQuery(await request.Content!.ReadAsStringAsync(cancellationToken)).ToDictionary(kv => kv.Key, kv => kv.Value.ToString());
        var call = new Call(request.RequestUri!.AbsolutePath, form);
        Calls.Add(call);
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(Answer(call)) };
    }
}

/// <summary>ZaloPay (sandbox): the signed order, the callback, the browser return, the status query and the refund.</summary>
public sealed class ZaloPayTests : PaymentTestBase
{
    private const string AppId = "2553";
    private const string Key1 = "key1_test_1234567890";
    private const string Key2 = "key2_test_1234567890";
    private const string Callback = "/api/v1/payments/zalopay/callback";
    private const string Return = "/api/v1/payments/zalopay/return";

    private static readonly FakeZaloPayApi Api = new();

    public ZaloPayTests()
        : base(services =>
        {
            services.PostConfigure<ZaloPayOptions>(o => o.Live = true);
            services.AddHttpClient(ZaloPayGateway.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => Api);
        })
    {
        Api.Calls.Clear();
        Api.Answer = call => call.Path == "/v2/create"
            ? new { return_code = 1, return_message = "Giao dịch thành công", order_url = $"https://qcgateway.zalopay.vn/openinapp?order={call.Form["app_trans_id"]}" }
            : new { return_code = 3, return_message = "Giao dịch đang xử lý", is_processing = true, amount = 0, zp_trans_id = 0 };
        Super.PostOk(Providers, new { code = "ZaloPay", displayName = "ZaloPay", mode = "Sandbox", merchantId = AppId, apiKey = Key1, secretKey = Key2 }).GetAwaiter().GetResult();
        SetProviderEnabledAsync("ZaloPay", true).GetAwaiter().GetResult();
        Admin.Put(Merchants, new { providerCode = "ZaloPay", isEnabled = true, merchantCode = "ZALOPAY" }).ShouldBe(201).GetAwaiter().GetResult();
    }

    private static string Hmac(string key, string data) =>
        Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(data))).ToLowerInvariant();

    private Task<JsonElement> StartAsync(Guid? order = null, decimal amount = 74_000) => PayOnlineAsync(order, amount, provider: "ZaloPay");

    private Task<JsonElement> ReadAsync(JsonElement payment) => GetAsync(Customer, $"{Tx}/{payment.Id()}");

    private static string AppTransIdOf() => Api.Calls.First(c => c.Path == "/v2/create").Form["app_trans_id"];

    /// <summary>A callback as ZaloPay posts it: the data as a JSON string, its mac with key2.</summary>
    private static object SignedCallback(string appTransId, long amount, long zpTransId = 240901000000123, string? mac = null, string appId = AppId)
    {
        var data = JsonSerializer.Serialize(new { app_id = long.Parse(appId), app_trans_id = appTransId, app_time = 1763190000000, app_user = "AICOS", amount, embed_data = "{}", item = "[]", zp_trans_id = zpTransId, server_time = 1763190005000, channel = 38, merchant_user_id = "u1", user_fee_amount = 0, discount_amount = 0 });
        return new { data, mac = mac ?? Hmac(Key2, data), type = 1 };
    }

    private async Task<JsonElement> CallbackAsync(object body) => await F.CreateClient().PostOk(Callback, body);

    [Fact]
    public async Task Starting_a_payment_creates_a_signed_ZaloPay_order_and_hands_its_page_to_the_customer()
    {
        var payment = await StartAsync(amount: 74_000);

        var call = Api.Calls.Single();
        call.Path.Should().Be("/v2/create");
        var f = call.Form;
        var appTransId = f["app_trans_id"];
        appTransId.Should().MatchRegex(@"^\d{6}_").And.EndWith(payment.GetProperty("transactionRef").GetString()!);
        (f["app_id"], f["amount"], f["item"]).Should().Be((AppId, "74000", "[]"));
        f["embed_data"].Should().Contain("http://localhost:8080/api/v1/payments/zalopay/return");
        f["callback_url"].Should().Be("http://localhost:8080/api/v1/payments/zalopay/callback");
        f["mac"].Should().Be(Hmac(Key1, $"{AppId}|{appTransId}|{f["app_user"]}|74000|{f["app_time"]}|{f["embed_data"]}|[]"));
        payment.GetProperty("checkoutUrl").GetString().Should().Be($"https://qcgateway.zalopay.vn/openinapp?order={appTransId}");
    }

    [Fact]
    public async Task When_ZaloPay_refuses_the_order_the_customer_is_told()
    {
        Api.Answer = _ => new { return_code = 2, return_message = "Giao dịch thất bại", sub_return_code = -401, sub_return_message = "Dữ liệu yêu cầu không hợp lệ" };

        await Customer.Post(Tx, new { orderId = Guid.NewGuid(), branchId = B1, amount = 74_000, providerCode = "ZaloPay" }).ShouldBe(422, "payment.zalopay_failed");
    }

    [Fact]
    public async Task A_signed_callback_marks_the_payment_paid_once()
    {
        var order = Guid.NewGuid();
        var payment = await StartAsync(order, 74_000);
        var appTransId = AppTransIdOf();

        (await CallbackAsync(SignedCallback(appTransId, 74_000))).GetProperty("return_code").GetInt32().Should().Be(1);
        (await ReadAsync(payment)).GetProperty("status").GetString().Should().Be("Succeeded");
        var ev = F.Events.Published.OfType<PaymentSucceededEvent>().Single();
        (ev.OrderId, ev.Amount, ev.Method).Should().Be((order, 74_000m, "ZaloPay"));

        (await CallbackAsync(SignedCallback(appTransId, 74_000))).GetProperty("return_code").GetInt32().Should().Be(2);
        F.Events.Published.OfType<PaymentSucceededEvent>().Should().HaveCount(1);
    }

    [Fact]
    public async Task A_forged_or_foreign_callback_is_refused_and_a_short_amount_does_not_pay()
    {
        var payment = await StartAsync(amount: 74_000);
        var appTransId = AppTransIdOf();

        (await CallbackAsync(SignedCallback(appTransId, 74_000, mac: new string('0', 64)))).GetProperty("return_code").GetInt32().Should().Be(-1);
        (await CallbackAsync(SignedCallback(appTransId, 74_000, appId: "9999"))).GetProperty("return_code").GetInt32().Should().Be(-1);
        (await CallbackAsync(SignedCallback(appTransId, 50_000))).GetProperty("return_code").GetInt32().Should().Be(1);
        (await ReadAsync(payment)).GetProperty("status").GetString().Should().Be("Pending");
        (await CallbackAsync(SignedCallback("260101_AICOSUNKNOWN00000", 2_000))).GetProperty("return_code").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task The_browser_return_asks_ZaloPay_and_redirects_to_the_result_page()
    {
        var order = Guid.NewGuid();
        var payment = await StartAsync(order, 74_000);
        var appTransId = AppTransIdOf();
        var browser = F.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        // The return query says status=1, but ZaloPay still says 3 (processing): nothing is paid.
        var claimed = await browser.GetAsync($"{Return}?appid={AppId}&apptransid={appTransId}&status=1&amount=74000&checksum=x");
        QueryHelpers.ParseQuery(new Uri(claimed.Headers.Location!.ToString()).Query)["status"].ToString().Should().Be("pending");
        var query = Api.Calls.Last();
        query.Path.Should().Be("/v2/query");
        (query.Form["app_trans_id"], query.Form["mac"]).Should().Be((appTransId, Hmac(Key1, $"{AppId}|{appTransId}|{Key1}")));

        Api.Answer = _ => new { return_code = 1, return_message = "Giao dịch thành công", is_processing = false, amount = 74_000, zp_trans_id = 240901000000123 };
        var back = await browser.GetAsync($"{Return}?apptransid={appTransId}");

        back.Headers.Location!.ToString().Should().StartWith("http://localhost:8443/thanh-toan/ket-qua?");
        var result = QueryHelpers.ParseQuery(new Uri(back.Headers.Location!.ToString()).Query);
        (result["status"].ToString(), result["paymentId"].ToString(), result["orderId"].ToString()).Should().Be(("paid", payment.Id().ToString(), order.ToString()));
        (await ReadAsync(payment)).GetProperty("status").GetString().Should().Be("Succeeded");

        var unknown = await browser.GetAsync($"{Return}?apptransid=nope");
        QueryHelpers.ParseQuery(new Uri(unknown.Headers.Location!.ToString()).Query)["status"].ToString().Should().Be("invalid");
    }

    [Fact]
    public async Task A_failed_payment_comes_back_failed_with_ZaloPay_s_reason()
    {
        var payment = await StartAsync(amount: 74_000);
        var appTransId = AppTransIdOf();
        Api.Answer = _ => new { return_code = 2, return_message = "Giao dịch thất bại", sub_return_code = -54, sub_return_message = "Giao dịch hết hạn", is_processing = false, amount = 74_000, zp_trans_id = 0 };
        var browser = F.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var back = await browser.GetAsync($"{Return}?apptransid={appTransId}&status=-49");

        var result = QueryHelpers.ParseQuery(new Uri(back.Headers.Location!.ToString()).Query);
        (result["status"].ToString(), result["reason"].ToString()).Should().Be(("failed", "ZaloPay: Giao dịch hết hạn (-54)."));
    }

    [Fact]
    public async Task A_refund_goes_back_through_ZaloPay_against_the_paid_transaction()
    {
        var payment = await StartAsync(amount: 74_000);
        await CallbackAsync(SignedCallback(AppTransIdOf(), 74_000, zpTransId: 240901000000123));
        Api.Answer = _ => new { return_code = 1, return_message = "Giao dịch thành công", refund_id = 777000111 };

        var refund = await Cashier1.PostOk($"{Tx}/{payment.Id()}/refunds", new { kind = "Cancellation", reason = "Khách hủy" });

        refund.GetProperty("status").GetString().Should().Be("Succeeded");
        var call = Api.Calls.Last();
        call.Path.Should().Be("/v2/refund");
        var f = call.Form;
        (f["zp_trans_id"], f["amount"], f["app_id"]).Should().Be(("240901000000123", "74000", AppId));
        f["m_refund_id"].Should().MatchRegex($@"^\d{{6}}_{AppId}_");
        f["mac"].Should().Be(Hmac(Key1, $"{AppId}|240901000000123|74000|{f["description"]}|{f["timestamp"]}"));
        (await ReadAsync(payment)).GetProperty("status").GetString().Should().Be("Refunded");
        F.Events.Published.OfType<PaymentRefundedEvent>().Single().Amount.Should().Be(74_000m);
    }

    [Fact]
    public async Task A_refund_ZaloPay_refuses_is_marked_failed()
    {
        var payment = await StartAsync(amount: 74_000);
        await CallbackAsync(SignedCallback(AppTransIdOf(), 74_000));
        Api.Answer = _ => new { return_code = 2, return_message = "Giao dịch thất bại", sub_return_code = -13, sub_return_message = "Số tiền hoàn không hợp lệ" };

        var refund = await Cashier1.PostOk($"{Tx}/{payment.Id()}/refunds", new { kind = "Cancellation", reason = "Khách hủy" });

        refund.GetProperty("status").GetString().Should().Be("Failed");
        (await ReadAsync(payment)).GetProperty("status").GetString().Should().Be("Succeeded");
    }
}

/// <summary>The failure simulation (testing only): what ZaloPay reports as paid is taken as refused, for the reason chosen in the settings.</summary>
public sealed class ZaloPaySimulatedFailureTests : PaymentTestBase
{
    private const string AppId = "2554";
    private const string Key1 = "key1_sim_1234567890";
    private const string Key2 = "key2_sim_1234567890";

    private static readonly FakeZaloPayApi Api = new();

    public ZaloPaySimulatedFailureTests()
        : base(services =>
        {
            services.PostConfigure<ZaloPayOptions>(o => { o.Live = true; o.SimulateFailure = "lost"; });
            services.AddHttpClient(ZaloPayGateway.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => Api);
        })
    {
        Api.Calls.Clear();
        Api.Answer = call => call.Path == "/v2/create"
            ? new { return_code = 1, return_message = "Giao dịch thành công", order_url = "https://qcgateway.zalopay.vn/openinapp?order=x" }
            : new { return_code = 1, return_message = "Giao dịch thành công", is_processing = false, amount = 74_000, zp_trans_id = 240901000000999 };
        Super.PostOk(Providers, new { code = "ZaloPay", displayName = "ZaloPay", mode = "Sandbox", merchantId = AppId, apiKey = Key1, secretKey = Key2 }).GetAwaiter().GetResult();
        SetProviderEnabledAsync("ZaloPay", true).GetAwaiter().GetResult();
        Admin.Put(Merchants, new { providerCode = "ZaloPay", isEnabled = true, merchantCode = "ZALOPAY" }).ShouldBe(201).GetAwaiter().GetResult();
    }

    [Fact]
    public async Task A_paid_order_comes_back_refused_for_the_simulated_reason_and_can_be_paid_again()
    {
        var order = Guid.NewGuid();
        var payment = await PayOnlineAsync(order, 74_000, provider: "ZaloPay");
        var appTransId = Api.Calls.First(c => c.Path == "/v2/create").Form["app_trans_id"];
        var browser = F.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var back = await browser.GetAsync($"/api/v1/payments/zalopay/return?apptransid={appTransId}&status=1");

        var result = QueryHelpers.ParseQuery(new Uri(back.Headers.Location!.ToString()).Query);
        (result["status"].ToString(), result["reason"].ToString()).Should().Be(("failed", "ZaloPay (giả lập): Thẻ bị mất hoặc bị khóa (-217)."));
        (await GetAsync(Customer, $"{Tx}/{payment.Id()}")).GetProperty("status").GetString().Should().Be("Failed");
        F.Events.Published.OfType<PaymentSucceededEvent>().Should().BeEmpty();

        var again = await PayOnlineAsync(order, 74_000, provider: "ZaloPay");
        again.GetProperty("status").GetString().Should().Be("Pending");
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("no_money", "ZaloPay (giả lập): Số dư tài khoản không đủ (-63).")]
    [InlineData("TIMEOUT", "ZaloPay (giả lập): Giao dịch quá thời gian chờ (-54).")]
    [InlineData("other", "ZaloPay (giả lập): Giao dịch thất bại (-1).")]
    public void The_simulated_reason_follows_the_setting(string? setting, string? reason) =>
        ZaloPay.SimulatedFailure(setting).Should().Be(reason);
}
