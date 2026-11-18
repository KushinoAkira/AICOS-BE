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

/// <summary>Stands in for the MoMo API: records each call and answers what the test says.</summary>
public sealed class FakeMomoApi : HttpMessageHandler
{
    public sealed record Call(string Path, JsonElement Body);

    public List<Call> Calls { get; } = [];

    public Func<Call, object> Answer { get; set; } = _ => new { resultCode = 99, message = "not set up" };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var call = new Call(request.RequestUri!.AbsolutePath, JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken)).RootElement.Clone());
        Calls.Add(call);
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(Answer(call)) };
    }
}

/// <summary>MoMo (test environment): the signed create request, the IPN, the browser return, the status query and the refund.</summary>
public sealed class MomoTests : PaymentTestBase
{
    private const string PartnerCode = "MOMOTEST01";
    private const string AccessKey = "access_key_1234";
    private const string SecretKey = "secret_key_1234567890";
    private const string Ipn = "/api/v1/payments/momo/ipn";
    private const string Return = "/api/v1/payments/momo/return";

    private static readonly FakeMomoApi Api = new();

    public MomoTests()
        : base(services =>
        {
            services.PostConfigure<MomoOptions>(o => o.Live = true);
            services.AddHttpClient(MomoGateway.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => Api);
        })
    {
        Api.Calls.Clear();
        Api.Answer = call => call.Path.EndsWith("/create")
            ? new { partnerCode = PartnerCode, orderId = call.Body.GetProperty("orderId").GetString(), resultCode = 0, message = "Thành công.", payUrl = $"https://test-payment.momo.vn/v2/gateway/pay?t={call.Body.GetProperty("orderId").GetString()}" }
            : new { partnerCode = PartnerCode, resultCode = 1000, message = "Giao dịch đã được khởi tạo, chờ người dùng xác nhận thanh toán.", amount = 0, transId = 0 };
        Super.PostOk(Providers, new { code = "Momo", displayName = "MoMo", mode = "Sandbox", merchantId = PartnerCode, apiKey = AccessKey, secretKey = SecretKey }).GetAwaiter().GetResult();
        SetProviderEnabledAsync("Momo", true).GetAwaiter().GetResult();
        Admin.Put(Merchants, new { providerCode = "Momo", isEnabled = true, merchantCode = "MOMO" }).ShouldBe(201).GetAwaiter().GetResult();
    }

    private static string Hmac(string data) =>
        Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(SecretKey), Encoding.UTF8.GetBytes(data))).ToLowerInvariant();

    /// <summary>An IPN as MoMo posts it, signed by hand over the fields MoMo documents, in its order.</summary>
    private static object SignedIpn(string orderId, long amount, int resultCode = 0, long transId = 4088878653, string? signature = null, string partner = PartnerCode)
    {
        const string extraData = "", orderInfo = "Thanh toan don AICOS", orderType = "momo_wallet", payType = "qr";
        var message = resultCode == 0 ? "Thành công." : "Giao dịch bị từ chối bởi người dùng.";
        const long responseTime = 1763190000000;
        var text = $"accessKey={AccessKey}&amount={amount}&extraData={extraData}&message={message}&orderId={orderId}&orderInfo={orderInfo}&orderType={orderType}"
            + $"&partnerCode={partner}&payType={payType}&requestId={orderId}&responseTime={responseTime}&resultCode={resultCode}&transId={transId}";
        return new { partnerCode = partner, orderId, requestId = orderId, amount, orderInfo, orderType, transId, resultCode, message, payType, responseTime, extraData, signature = signature ?? Hmac(text) };
    }

    private Task<JsonElement> StartAsync(Guid? order = null, decimal amount = 74_000) => PayOnlineAsync(order, amount, provider: "Momo");

    private Task<JsonElement> ReadAsync(JsonElement payment) => GetAsync(Customer, $"{Tx}/{payment.Id()}");

    private static string RefOf(JsonElement payment) => payment.GetProperty("transactionRef").GetString()!;

    private Task<HttpResponseMessage> PostIpnAsync(object body) => F.CreateClient().PostAsJsonAsync(Ipn, body);

    [Fact]
    public async Task Starting_a_payment_asks_MoMo_for_a_signed_link_and_hands_it_to_the_customer()
    {
        var payment = await StartAsync(amount: 74_000);

        var call = Api.Calls.Single();
        call.Path.Should().Be("/v2/gateway/api/create");
        var b = call.Body;
        var orderId = RefOf(payment);
        (b.GetProperty("partnerCode").GetString(), b.GetProperty("orderId").GetString(), b.GetProperty("amount").GetInt64()).Should().Be((PartnerCode, orderId, 74_000L));
        var redirect = b.GetProperty("redirectUrl").GetString()!;
        var ipn = b.GetProperty("ipnUrl").GetString()!;
        (redirect, ipn).Should().Be(("http://localhost:8080/api/v1/payments/momo/return", "http://localhost:8080/api/v1/payments/momo/ipn"));
        var expected = Hmac($"accessKey={AccessKey}&amount=74000&extraData=&ipnUrl={ipn}&orderId={orderId}&orderInfo={b.GetProperty("orderInfo").GetString()}"
            + $"&partnerCode={PartnerCode}&redirectUrl={redirect}&requestId={b.GetProperty("requestId").GetString()}&requestType={b.GetProperty("requestType").GetString()}");
        b.GetProperty("signature").GetString().Should().Be(expected);
        payment.GetProperty("checkoutUrl").GetString().Should().Be($"https://test-payment.momo.vn/v2/gateway/pay?t={orderId}");
    }

    [Fact]
    public async Task When_MoMo_refuses_the_link_the_customer_is_told()
    {
        Api.Answer = _ => new { resultCode = 22, message = "Số tiền giao dịch không hợp lệ." };

        await Customer.Post(Tx, new { orderId = Guid.NewGuid(), branchId = B1, amount = 74_000, providerCode = "Momo" }).ShouldBe(422, "payment.momo_failed");
    }

    [Fact]
    public async Task A_signed_IPN_marks_the_payment_paid_once()
    {
        var order = Guid.NewGuid();
        var payment = await StartAsync(order, 74_000);

        (await PostIpnAsync(SignedIpn(RefOf(payment), 74_000))).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await ReadAsync(payment)).GetProperty("status").GetString().Should().Be("Succeeded");
        var ev = F.Events.Published.OfType<PaymentSucceededEvent>().Single();
        (ev.OrderId, ev.Amount, ev.Method).Should().Be((order, 74_000m, "Momo"));

        (await PostIpnAsync(SignedIpn(RefOf(payment), 74_000))).StatusCode.Should().Be(HttpStatusCode.NoContent);
        F.Events.Published.OfType<PaymentSucceededEvent>().Should().HaveCount(1);
    }

    [Fact]
    public async Task A_forged_or_foreign_IPN_is_refused_and_a_short_amount_does_not_pay()
    {
        var payment = await StartAsync(amount: 74_000);

        (await PostIpnAsync(SignedIpn(RefOf(payment), 74_000, signature: new string('0', 64)))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await PostIpnAsync(SignedIpn(RefOf(payment), 74_000, partner: "OTHERPARTNER"))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await PostIpnAsync(SignedIpn(RefOf(payment), 50_000))).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await ReadAsync(payment)).GetProperty("status").GetString().Should().Be("Pending");
        (await PostIpnAsync(SignedIpn("AICOSUNKNOWN0000000", 2_000))).StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task A_declined_payment_fails_the_attempt()
    {
        var payment = await StartAsync(amount: 74_000);

        await PostIpnAsync(SignedIpn(RefOf(payment), 74_000, resultCode: 1006));

        var read = await ReadAsync(payment);
        read.GetProperty("status").GetString().Should().Be("Failed");
        F.Events.Published.OfType<PaymentFailedEvent>().Should().ContainSingle();
    }

    [Fact]
    public async Task The_browser_return_asks_MoMo_and_redirects_to_the_result_page()
    {
        var order = Guid.NewGuid();
        var payment = await StartAsync(order, 74_000);
        var orderId = RefOf(payment);
        var browser = F.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        // The return query says resultCode=0, but MoMo still says 1000 (waiting): nothing is paid.
        var claimed = await browser.GetAsync($"{Return}?partnerCode={PartnerCode}&orderId={orderId}&resultCode=0&amount=74000");
        QueryHelpers.ParseQuery(new Uri(claimed.Headers.Location!.ToString()).Query)["status"].ToString().Should().Be("pending");
        var query = Api.Calls.Last();
        query.Path.Should().Be("/v2/gateway/api/query");
        query.Body.GetProperty("signature").GetString().Should().Be(Hmac($"accessKey={AccessKey}&orderId={orderId}&partnerCode={PartnerCode}&requestId={query.Body.GetProperty("requestId").GetString()}"));

        Api.Answer = _ => new { partnerCode = PartnerCode, orderId, resultCode = 0, message = "Thành công.", amount = 74_000, transId = 4088878653 };
        var back = await browser.GetAsync($"{Return}?orderId={orderId}");

        back.Headers.Location!.ToString().Should().StartWith("http://localhost:8443/thanh-toan/ket-qua?");
        var result = QueryHelpers.ParseQuery(new Uri(back.Headers.Location!.ToString()).Query);
        (result["status"].ToString(), result["paymentId"].ToString(), result["orderId"].ToString()).Should().Be(("paid", payment.Id().ToString(), order.ToString()));
        (await ReadAsync(payment)).GetProperty("status").GetString().Should().Be("Succeeded");

        var unknown = await browser.GetAsync($"{Return}?orderId=NOPE");
        QueryHelpers.ParseQuery(new Uri(unknown.Headers.Location!.ToString()).Query)["status"].ToString().Should().Be("invalid");
    }

    [Fact]
    public async Task A_cancelled_payment_comes_back_failed_with_MoMo_s_reason()
    {
        var payment = await StartAsync(amount: 74_000);
        Api.Answer = _ => new { partnerCode = PartnerCode, orderId = RefOf(payment), resultCode = 1006, message = "Giao dịch bị từ chối bởi người dùng.", amount = 74_000, transId = 0 };
        var browser = F.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var back = await browser.GetAsync($"{Return}?orderId={RefOf(payment)}&resultCode=1006");

        var result = QueryHelpers.ParseQuery(new Uri(back.Headers.Location!.ToString()).Query);
        (result["status"].ToString(), result["reason"].ToString()).Should().Be(("failed", "MoMo: Giao dịch bị từ chối bởi người dùng. (1006)."));
    }

    [Fact]
    public async Task A_refund_goes_back_through_MoMo_against_the_paid_transaction()
    {
        var order = Guid.NewGuid();
        var payment = await StartAsync(order, 74_000);
        await PostIpnAsync(SignedIpn(RefOf(payment), 74_000, transId: 4088878653));
        Api.Answer = call => new { partnerCode = PartnerCode, orderId = call.Body.GetProperty("orderId").GetString(), resultCode = 0, message = "Thành công.", amount = 74_000, transId = 4088880000 };

        var refund = await Cashier1.PostOk($"{Tx}/{payment.Id()}/refunds", new { kind = "Cancellation", reason = "Khách hủy" });

        refund.GetProperty("status").GetString().Should().Be("Succeeded");
        var call = Api.Calls.Last();
        call.Path.Should().Be("/v2/gateway/api/refund");
        var b = call.Body;
        (b.GetProperty("transId").GetInt64(), b.GetProperty("amount").GetInt64()).Should().Be((4088878653L, 74_000L));
        b.GetProperty("orderId").GetString().Should().NotBe(RefOf(payment));   // MoMo wants a new id for the refund
        b.GetProperty("signature").GetString().Should().Be(Hmac(
            $"accessKey={AccessKey}&amount=74000&description={b.GetProperty("description").GetString()}&orderId={b.GetProperty("orderId").GetString()}&partnerCode={PartnerCode}&requestId={b.GetProperty("requestId").GetString()}&transId=4088878653"));
        (await ReadAsync(payment)).GetProperty("status").GetString().Should().Be("Refunded");
        F.Events.Published.OfType<PaymentRefundedEvent>().Single().Amount.Should().Be(74_000m);
    }

    [Fact]
    public async Task A_refund_MoMo_refuses_is_marked_failed()
    {
        var payment = await StartAsync(amount: 74_000);
        await PostIpnAsync(SignedIpn(RefOf(payment), 74_000));
        Api.Answer = _ => new { partnerCode = PartnerCode, resultCode = 1080, message = "Giao dịch hoàn tiền bị từ chối." };

        var refund = await Cashier1.PostOk($"{Tx}/{payment.Id()}/refunds", new { kind = "Cancellation", reason = "Khách hủy" });

        refund.GetProperty("status").GetString().Should().Be("Failed");
        (await ReadAsync(payment)).GetProperty("status").GetString().Should().Be("Succeeded");
    }
}
