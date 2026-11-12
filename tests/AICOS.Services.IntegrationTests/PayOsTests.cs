using System.Globalization;
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

/// <summary>Stands in for the payOS API: records each call and answers what the test says.</summary>
public sealed class FakePayOsApi : HttpMessageHandler
{
    public sealed record Call(HttpMethod Method, string Path, string? ClientId, string? ApiKey, JsonElement? Body);

    public List<Call> Calls { get; } = [];

    public Func<Call, object> Answer { get; set; } = _ => new { code = "20", desc = "not set up" };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        JsonElement? body = request.Content is null ? null : JsonDocument.Parse(await request.Content.ReadAsStringAsync(cancellationToken)).RootElement.Clone();
        var call = new Call(
            request.Method, request.RequestUri!.AbsolutePath, request.Headers.TryGetValues("x-client-id", out var id) ? id.Single() : null,
            request.Headers.TryGetValues("x-api-key", out var key) ? key.Single() : null, body);
        Calls.Add(call);
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(Answer(call)) };
    }
}

/// <summary>payOS: the signed payment request, the webhook (checked against payOS's own test vectors), the browser return and the status.</summary>
public sealed class PayOsTests : PaymentTestBase
{
    // The checksum key of payOS's published webhook test vectors (payos-lib-golang, testdata/webhook-vectors.json, MIT).
    private const string ChecksumKey = "payos_sec_2026_01_vector_key";
    private const string ClientId = "client-123";
    private const string ApiKey = "api-key-456";
    private const string Webhook = "/api/v1/payments/payos/webhook";
    private const string Return = "/api/v1/payments/payos/return";

    private static readonly FakePayOsApi Api = new();

    public PayOsTests()
        : base(services => services.AddHttpClient(PayOsGateway.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => Api))
    {
        Api.Calls.Clear();
        Api.Answer = call => call.Method == HttpMethod.Post
            ? new { code = "00", desc = "success", data = new { checkoutUrl = $"https://pay.payos.vn/web/link{call.Body!.Value.GetProperty("orderCode").GetInt64()}", status = "PENDING" } }
            : new { code = "00", desc = "success", data = new { status = "PENDING", amountPaid = 0 } };
        Super.PostOk(Providers, new { code = "PayOS", displayName = "payOS", mode = "Sandbox", merchantId = ClientId, apiKey = ApiKey, secretKey = ChecksumKey }).GetAwaiter().GetResult();
        SetProviderEnabledAsync("PayOS", true).GetAwaiter().GetResult();
        Admin.Put(Merchants, new { providerCode = "PayOS", isEnabled = true }).ShouldBe(201).GetAwaiter().GetResult();
    }

    private static string Hmac(string data) =>
        Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(ChecksumKey), Encoding.UTF8.GetBytes(data))).ToLowerInvariant();

    private static long OrderCodeOf(JsonElement payment) =>
        long.Parse(payment.GetProperty("transactionRef").GetString()![5..18], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);

    /// <summary>A webhook as payOS sends it for a transfer, signed by hand: every data field, sorted by name, as key=value joined with &amp;.</summary>
    private static object Signed(long orderCode, long amount, string reference = "FT26303123456", string code = "00", string? signature = null)
    {
        var description = $"AICOS {orderCode}";
        var text = $"accountNumber=0123456789&amount={amount}&code={code}&counterAccountBankId=&counterAccountBankName=&counterAccountName=&counterAccountNumber="
            + $"&currency=VND&desc=success&description={description}&orderCode={orderCode}&paymentLinkId=plink{orderCode}&reference={reference}"
            + "&transactionDateTime=2026-11-12 21:00:00&virtualAccountName=&virtualAccountNumber=";
        return new
        {
            code = "00", desc = "success", success = true,
            data = new
            {
                accountNumber = "0123456789", amount, code, counterAccountBankId = (string?)null, counterAccountBankName = (string?)null, counterAccountName = (string?)null,
                counterAccountNumber = (string?)null, currency = "VND", desc = "success", description, orderCode, paymentLinkId = $"plink{orderCode}", reference,
                transactionDateTime = "2026-11-12 21:00:00", virtualAccountName = "", virtualAccountNumber = "",
            },
            signature = signature ?? Hmac(text),
        };
    }

    private async Task<JsonElement> HookAsync(object body) => await F.CreateClient().PostOk(Webhook, body);

    private async Task<JsonElement> StartAsync(Guid? order = null, decimal amount = 74_000) => await PayOnlineAsync(order, amount, provider: "PayOS");

    private Task<JsonElement> ReadAsync(JsonElement payment) => GetAsync(Customer, $"{Tx}/{payment.Id()}");

    [Fact]
    public async Task Starting_a_payment_asks_payOS_for_a_signed_link_and_hands_it_to_the_customer()
    {
        var payment = await StartAsync(amount: 74_000);

        var call = Api.Calls.Single();
        (call.Method, call.Path, call.ClientId, call.ApiKey).Should().Be((HttpMethod.Post, "/v2/payment-requests", ClientId, ApiKey));
        var body = call.Body!.Value;
        var orderCode = OrderCodeOf(payment);
        body.GetProperty("orderCode").GetInt64().Should().Be(orderCode);
        body.GetProperty("amount").GetInt64().Should().Be(74_000);
        var description = body.GetProperty("description").GetString()!;
        description.Length.Should().BeLessThanOrEqualTo(9);
        var back = body.GetProperty("returnUrl").GetString()!;
        back.Should().Be("http://localhost:8080/api/v1/payments/payos/return");
        body.GetProperty("cancelUrl").GetString().Should().Be(back);
        body.GetProperty("expiredAt").GetInt64().Should().Be(F.Clock.Now.AddMinutes(15).ToUnixTimeSeconds());
        body.GetProperty("signature").GetString().Should().Be(Hmac($"amount=74000&cancelUrl={back}&description={description}&orderCode={orderCode}&returnUrl={back}"));
        payment.GetProperty("checkoutUrl").GetString().Should().Be($"https://pay.payos.vn/web/link{orderCode}");
    }

    [Fact]
    public async Task When_payOS_refuses_the_link_the_customer_is_told()
    {
        Api.Answer = _ => new { code = "231", desc = "Đơn thanh toán đã tồn tại" };

        await Customer.Post(Tx, new { orderId = Guid.NewGuid(), branchId = B1, amount = 74_000, providerCode = "PayOS" }).ShouldBe(422, "payment.payos_failed");
    }

    [Fact]
    public async Task The_webhook_marks_the_payment_paid_once()
    {
        var order = Guid.NewGuid();
        var payment = await StartAsync(order, 74_000);
        var code = OrderCodeOf(payment);

        (await HookAsync(Signed(code, 74_000))).GetProperty("outcome").GetString().Should().Be("applied");
        (await ReadAsync(payment)).GetProperty("status").GetString().Should().Be("Succeeded");
        var ev = F.Events.Published.OfType<PaymentSucceededEvent>().Single();
        (ev.OrderId, ev.Amount, ev.Method).Should().Be((order, 74_000m, "PayOS"));

        var again = await HookAsync(Signed(code, 74_000));
        again.GetProperty("duplicate").GetBoolean().Should().BeTrue();
        F.Events.Published.OfType<PaymentSucceededEvent>().Should().HaveCount(1);
    }

    [Fact]
    public async Task A_transfer_of_less_than_the_amount_does_not_pay_the_order()
    {
        var payment = await StartAsync(amount: 74_000);

        (await HookAsync(Signed(OrderCodeOf(payment), 50_000))).GetProperty("outcome").GetString().Should().Be("amount_mismatch");
        (await ReadAsync(payment)).GetProperty("status").GetString().Should().Be("Pending");
    }

    [Fact]
    public async Task A_webhook_with_a_wrong_signature_is_refused_and_one_for_an_unknown_order_is_acknowledged()
    {
        var payment = await StartAsync(amount: 74_000);

        await F.CreateClient().Post(Webhook, Signed(OrderCodeOf(payment), 74_000, signature: new string('0', 64))).ShouldBe(401);
        (await ReadAsync(payment)).GetProperty("status").GetString().Should().Be("Pending");
        (await HookAsync(Signed(123, 2_000))).GetProperty("outcome").GetString().Should().Be("unknown_payment");   // payOS's test call when the webhook URL is saved
    }

    public static TheoryData<string, string, string> Vectors => new()
    {
        { "A1_full_payload_ampersand_in_name", "accept", """{"code":"00","desc":"success","success":true,"data":{"accountNumber":"0123456789","amount":2000,"code":"00","counterAccountBankId":"970422","counterAccountBankName":"MBBANK","counterAccountName":"CONG TY TNHH A&B","counterAccountNumber":"9876543210","currency":"VND","desc":"success","description":"thanh toan don 100001","orderCode":100001,"paymentLinkId":"3f2b9c1e8d7a4b6c9e0f1a2b3c4d5e6f","reference":"FT26303123456","transactionDateTime":"2026-10-30 10:00:00","virtualAccountName":"","virtualAccountNumber":""},"signature":"9e98c1efc7bdb272004917fbbfc44eac672d3e33b534fc890080a7c5b76893cd"}""" },
        { "A2_optional_fields_null", "accept", """{"code":"00","desc":"success","success":true,"data":{"accountNumber":"0123456789","amount":2000,"code":"00","counterAccountBankId":null,"counterAccountBankName":null,"counterAccountName":null,"counterAccountNumber":null,"currency":"VND","desc":"success","description":"thanh toan don 100001","orderCode":100001,"paymentLinkId":"3f2b9c1e8d7a4b6c9e0f1a2b3c4d5e6f","reference":"FT26303123456","transactionDateTime":"2026-10-30 10:00:00","virtualAccountName":null,"virtualAccountNumber":null},"signature":"6bc1ad6fc0f98d4bad9cf25773818c763222dc7bdc3b981a2fc649c48838679c"}""" },
        { "A3_optional_fields_absent", "accept", """{"code":"00","desc":"success","success":true,"data":{"accountNumber":"0123456789","amount":2000,"code":"00","currency":"VND","desc":"success","description":"thanh toan don 100001","orderCode":100001,"paymentLinkId":"3f2b9c1e8d7a4b6c9e0f1a2b3c4d5e6f","reference":"FT26303123456","transactionDateTime":"2026-10-30 10:00:00"},"signature":"a7b85507c2080b29d3db3fefaf5ae5adae0e8b89e9a363bda48545b5f5fe5222"}""" },
        { "A4_unknown_keys_allowed", "accept", """{"code":"00","desc":"success","success":true,"data":{"accountNumber":"0123456789","amount":2000,"code":"00","counterAccountBankId":"970422","counterAccountBankName":"MBBANK","counterAccountName":"CONG TY TNHH A&B","counterAccountNumber":"9876543210","currency":"VND","desc":"success","description":"thanh toan don 100001","orderCode":100001,"paymentLinkId":"3f2b9c1e8d7a4b6c9e0f1a2b3c4d5e6f","reference":"FT26303123456","transactionDateTime":"2026-10-30 10:00:00","virtualAccountName":"","virtualAccountNumber":"","newField":"abc","newCount":5,"newNullable":null},"signature":"32ee2b700ee99fed2258b583e64f3e4f1f98762c1d974253a7fe0aa731974736"}""" },
        { "A5_zero_order_code_empty_strings", "accept", """{"code":"00","desc":"success","success":true,"data":{"accountNumber":"0123456789","amount":2000,"code":"00","counterAccountBankId":"970422","counterAccountBankName":"MBBANK","counterAccountName":"","counterAccountNumber":"9876543210","currency":"VND","desc":"success","description":"","orderCode":0,"paymentLinkId":"3f2b9c1e8d7a4b6c9e0f1a2b3c4d5e6f","reference":"FT26303123456","transactionDateTime":"2026-10-30 10:00:00","virtualAccountName":"","virtualAccountNumber":""},"signature":"00e3264aba305f94df6759935f30d8a2a47c15ff143808429daa30587fef4c72"}""" },
        { "R01_delimiter_smuggling", "reject", """{"code":"00","desc":"success","success":true,"data":{"accountNumber":"0123456789","amount":2000,"code":"00","counterAccountBankId":"970422","counterAccountBankName":"MBBANK","counterAccountName":"CONG TY TNHH A&B","counterAccountNumber":"9876543210","currency":"VND","desc":"success","description":"pay","orderCode":999999,"paymentLinkId":"forged&orderCode=100001&paymentLinkId=plink_100001","reference":"FT26303123456","transactionDateTime":"2026-10-30 10:00:00","virtualAccountName":"","virtualAccountNumber":""},"signature":"7503c0b7adf010b952e3d9e5b6d52521f92216d39b20e76f061fd18e403eda13"}""" },
        { "R02_nested_object_value", "reject", """{"code":"00","desc":"success","success":true,"data":{"accountNumber":"0123456789","amount":2000,"code":"00","counterAccountBankId":"970422","counterAccountBankName":"MBBANK","counterAccountName":"CONG TY TNHH A&B","counterAccountNumber":"9876543210","currency":"VND","desc":"success","description":{"$ne":null,"injected":true},"orderCode":100001,"paymentLinkId":"3f2b9c1e8d7a4b6c9e0f1a2b3c4d5e6f","reference":"FT26303123456","transactionDateTime":"2026-10-30 10:00:00","virtualAccountName":"","virtualAccountNumber":""},"signature":"34d17fead8007ef272b582c18da27eae9ff787f0569858aa6362b5189ee6b771"}""" },
        { "R03_order_code_as_string", "reject", """{"code":"00","desc":"success","success":true,"data":{"accountNumber":"0123456789","amount":2000,"code":"00","counterAccountBankId":"970422","counterAccountBankName":"MBBANK","counterAccountName":"CONG TY TNHH A&B","counterAccountNumber":"9876543210","currency":"VND","desc":"success","description":"thanh toan don 100001","orderCode":"100001","paymentLinkId":"3f2b9c1e8d7a4b6c9e0f1a2b3c4d5e6f","reference":"FT26303123456","transactionDateTime":"2026-10-30 10:00:00","virtualAccountName":"","virtualAccountNumber":""},"signature":"9e98c1efc7bdb272004917fbbfc44eac672d3e33b534fc890080a7c5b76893cd"}""" },
        { "R04_amount_boolean", "reject", """{"code":"00","desc":"success","success":true,"data":{"accountNumber":"0123456789","amount":true,"code":"00","counterAccountBankId":"970422","counterAccountBankName":"MBBANK","counterAccountName":"CONG TY TNHH A&B","counterAccountNumber":"9876543210","currency":"VND","desc":"success","description":"thanh toan don 100001","orderCode":100001,"paymentLinkId":"3f2b9c1e8d7a4b6c9e0f1a2b3c4d5e6f","reference":"FT26303123456","transactionDateTime":"2026-10-30 10:00:00","virtualAccountName":"","virtualAccountNumber":""},"signature":"d01f715aa5446915de5395eefe567dacef3da03c3ea2ebe4cb5c441e565e2066"}""" },
        { "R05_amount_fraction", "reject", """{"code":"00","desc":"success","success":true,"data":{"accountNumber":"0123456789","amount":1.5,"code":"00","counterAccountBankId":"970422","counterAccountBankName":"MBBANK","counterAccountName":"CONG TY TNHH A&B","counterAccountNumber":"9876543210","currency":"VND","desc":"success","description":"thanh toan don 100001","orderCode":100001,"paymentLinkId":"3f2b9c1e8d7a4b6c9e0f1a2b3c4d5e6f","reference":"FT26303123456","transactionDateTime":"2026-10-30 10:00:00","virtualAccountName":"","virtualAccountNumber":""},"signature":"292db369e8264b56d169a87cea3481f341b4119005b8845fc4c60b80f5f2b0f1"}""" },
        { "R06_core_string_contains_equals", "reject", """{"code":"00","desc":"success","success":true,"data":{"accountNumber":"0123456789","amount":2000,"code":"00","counterAccountBankId":"970422","counterAccountBankName":"MBBANK","counterAccountName":"CONG TY TNHH A&B","counterAccountNumber":"9876543210","currency":"VND","desc":"success","description":"thanh toan don 100001","orderCode":100001,"paymentLinkId":"3f2b9c1e8d7a4b6c9e0f1a2b3c4d5e6f","reference":"FT=1","transactionDateTime":"2026-10-30 10:00:00","virtualAccountName":"","virtualAccountNumber":""},"signature":"316e7835507502b938b137fe00863edde3956ac303dea6593168d2a6640211aa"}""" },
        { "R07_description_as_number", "reject", """{"code":"00","desc":"success","success":true,"data":{"accountNumber":"0123456789","amount":2000,"code":"00","counterAccountBankId":"970422","counterAccountBankName":"MBBANK","counterAccountName":"CONG TY TNHH A&B","counterAccountNumber":"9876543210","currency":"VND","desc":"success","description":123,"orderCode":100001,"paymentLinkId":"3f2b9c1e8d7a4b6c9e0f1a2b3c4d5e6f","reference":"FT26303123456","transactionDateTime":"2026-10-30 10:00:00","virtualAccountName":"","virtualAccountNumber":""},"signature":"dfe3274fb705772c9ab53cd7ab6641cd33d52731c89d396c93f7a94c86efbf10"}""" },
        { "R08_array_value", "reject", """{"code":"00","desc":"success","success":true,"data":{"accountNumber":"0123456789","amount":2000,"code":"00","counterAccountBankId":"970422","counterAccountBankName":"MBBANK","counterAccountName":"CONG TY TNHH A&B","counterAccountNumber":"9876543210","currency":"VND","desc":"success","description":["a"],"orderCode":100001,"paymentLinkId":"3f2b9c1e8d7a4b6c9e0f1a2b3c4d5e6f","reference":"FT26303123456","transactionDateTime":"2026-10-30 10:00:00","virtualAccountName":"","virtualAccountNumber":""},"signature":"ea6fee8fa42b74fb7c86e280fc301dacef1ff822d4c2455ad567e0a8f895cf66"}""" },
        { "R09_missing_core_key", "reject", """{"code":"00","desc":"success","success":true,"data":{"accountNumber":"0123456789","amount":2000,"code":"00","counterAccountBankId":"970422","counterAccountBankName":"MBBANK","counterAccountName":"CONG TY TNHH A&B","counterAccountNumber":"9876543210","currency":"VND","desc":"success","description":"thanh toan don 100001","orderCode":100001,"paymentLinkId":"3f2b9c1e8d7a4b6c9e0f1a2b3c4d5e6f","transactionDateTime":"2026-10-30 10:00:00","virtualAccountName":"","virtualAccountNumber":""},"signature":"ec5ef644fa2ee029e5fb1c0b26f47fdcc9e98b140c685c737bd759b5602b7892"}""" },
        { "R10_proto_key", "reject", """{"code":"00","desc":"success","success":true,"data":{"__proto__":"x","accountNumber":"0123456789","amount":2000,"code":"00","counterAccountBankId":"970422","counterAccountBankName":"MBBANK","counterAccountName":"CONG TY TNHH A&B","counterAccountNumber":"9876543210","currency":"VND","desc":"success","description":"thanh toan don 100001","orderCode":100001,"paymentLinkId":"3f2b9c1e8d7a4b6c9e0f1a2b3c4d5e6f","reference":"FT26303123456","transactionDateTime":"2026-10-30 10:00:00","virtualAccountName":"","virtualAccountNumber":""},"signature":"9e98c1efc7bdb272004917fbbfc44eac672d3e33b534fc890080a7c5b76893cd"}""" },
        { "R11_unknown_key_value_contains_equals", "reject", """{"code":"00","desc":"success","success":true,"data":{"accountNumber":"0123456789","amount":2000,"code":"00","counterAccountBankId":"970422","counterAccountBankName":"MBBANK","counterAccountName":"CONG TY TNHH A&B","counterAccountNumber":"9876543210","currency":"VND","desc":"success","description":"thanh toan don 100001","orderCode":100001,"paymentLinkId":"3f2b9c1e8d7a4b6c9e0f1a2b3c4d5e6f","reference":"FT26303123456","transactionDateTime":"2026-10-30 10:00:00","virtualAccountName":"","virtualAccountNumber":"","newField":"a=b"},"signature":"3c9c4d0fe03a8b8b8e13328602b28ee9e5ca8422303ec7bdd0ea2819fbc22793"}""" },
        { "R12_unknown_key_invalid_name", "reject", """{"code":"00","desc":"success","success":true,"data":{"accountNumber":"0123456789","amount":2000,"code":"00","counterAccountBankId":"970422","counterAccountBankName":"MBBANK","counterAccountName":"CONG TY TNHH A&B","counterAccountNumber":"9876543210","currency":"VND","desc":"success","description":"thanh toan don 100001","orderCode":100001,"paymentLinkId":"3f2b9c1e8d7a4b6c9e0f1a2b3c4d5e6f","reference":"FT26303123456","transactionDateTime":"2026-10-30 10:00:00","virtualAccountName":"","virtualAccountNumber":"","bad-key":"x"},"signature":"bd04f9c20d094ca3027cf92c846a134e82c3c2735b6df739b643d9719d7c8572"}""" },
        { "R13_unknown_key_object_value", "reject", """{"code":"00","desc":"success","success":true,"data":{"accountNumber":"0123456789","amount":2000,"code":"00","counterAccountBankId":"970422","counterAccountBankName":"MBBANK","counterAccountName":"CONG TY TNHH A&B","counterAccountNumber":"9876543210","currency":"VND","desc":"success","description":"thanh toan don 100001","orderCode":100001,"paymentLinkId":"3f2b9c1e8d7a4b6c9e0f1a2b3c4d5e6f","reference":"FT26303123456","transactionDateTime":"2026-10-30 10:00:00","virtualAccountName":"","virtualAccountNumber":"","newObj":{"a":1}},"signature":"da75fdfe7724806445a1e46cbfea8038590071a2b4f831e2b644c20d92d2debf"}""" },
        { "R14_signature_mismatch", "reject", """{"code":"00","desc":"success","success":true,"data":{"accountNumber":"0123456789","amount":2000,"code":"00","counterAccountBankId":"970422","counterAccountBankName":"MBBANK","counterAccountName":"CONG TY TNHH A&B","counterAccountNumber":"9876543210","currency":"VND","desc":"success","description":"thanh toan don 100001","orderCode":100001,"paymentLinkId":"3f2b9c1e8d7a4b6c9e0f1a2b3c4d5e6f","reference":"FT26303123456","transactionDateTime":"2026-10-30 10:00:00","virtualAccountName":"","virtualAccountNumber":""},"signature":"0000000000000000000000000000000000000000000000000000000000000000"}""" },
        { "R15_signature_wrong_length", "reject", """{"code":"00","desc":"success","success":true,"data":{"accountNumber":"0123456789","amount":2000,"code":"00","counterAccountBankId":"970422","counterAccountBankName":"MBBANK","counterAccountName":"CONG TY TNHH A&B","counterAccountNumber":"9876543210","currency":"VND","desc":"success","description":"thanh toan don 100001","orderCode":100001,"paymentLinkId":"3f2b9c1e8d7a4b6c9e0f1a2b3c4d5e6f","reference":"FT26303123456","transactionDateTime":"2026-10-30 10:00:00","virtualAccountName":"","virtualAccountNumber":""},"signature":"abc"}""" },
        { "A6_amount_max_safe_integer", "accept", """{"code":"00","desc":"success","success":true,"data":{"accountNumber":"0123456789","amount":9007199254740991,"code":"00","counterAccountBankId":"970422","counterAccountBankName":"MBBANK","counterAccountName":"CONG TY TNHH A&B","counterAccountNumber":"9876543210","currency":"VND","desc":"success","description":"thanh toan don 100001","orderCode":100001,"paymentLinkId":"3f2b9c1e8d7a4b6c9e0f1a2b3c4d5e6f","reference":"FT26303123456","transactionDateTime":"2026-10-30 10:00:00","virtualAccountName":"","virtualAccountNumber":""},"signature":"0413e12fc1e14fc7ce4dc84fa783565340ac387bc5c3183253c47945311342d8"}""" },
        { "R16_amount_above_safe_integer", "reject", """{"code":"00","desc":"success","success":true,"data":{"accountNumber":"0123456789","amount":9007199254740992,"code":"00","counterAccountBankId":"970422","counterAccountBankName":"MBBANK","counterAccountName":"CONG TY TNHH A&B","counterAccountNumber":"9876543210","currency":"VND","desc":"success","description":"thanh toan don 100001","orderCode":100001,"paymentLinkId":"3f2b9c1e8d7a4b6c9e0f1a2b3c4d5e6f","reference":"FT26303123456","transactionDateTime":"2026-10-30 10:00:00","virtualAccountName":"","virtualAccountNumber":""},"signature":"351ca3a7a78784d8ecf670abdb5bf8fa955095527af64babe5b03658ef1a1d5d"}""" },
        { "R17_amount_below_safe_integer", "reject", """{"code":"00","desc":"success","success":true,"data":{"accountNumber":"0123456789","amount":-9007199254740992,"code":"00","counterAccountBankId":"970422","counterAccountBankName":"MBBANK","counterAccountName":"CONG TY TNHH A&B","counterAccountNumber":"9876543210","currency":"VND","desc":"success","description":"thanh toan don 100001","orderCode":100001,"paymentLinkId":"3f2b9c1e8d7a4b6c9e0f1a2b3c4d5e6f","reference":"FT26303123456","transactionDateTime":"2026-10-30 10:00:00","virtualAccountName":"","virtualAccountNumber":""},"signature":"a6b2095b1085fe671cd527e0b41aafa4551782eabd0432e8445e8c7e7730fc59"}""" },
        { "R18_order_code_int64_min", "reject", """{"code":"00","desc":"success","success":true,"data":{"accountNumber":"0123456789","amount":2000,"code":"00","counterAccountBankId":"970422","counterAccountBankName":"MBBANK","counterAccountName":"CONG TY TNHH A&B","counterAccountNumber":"9876543210","currency":"VND","desc":"success","description":"thanh toan don 100001","orderCode":-9223372036854775808,"paymentLinkId":"3f2b9c1e8d7a4b6c9e0f1a2b3c4d5e6f","reference":"FT26303123456","transactionDateTime":"2026-10-30 10:00:00","virtualAccountName":"","virtualAccountNumber":""},"signature":"404fc8f70020ee8bca036cb8816b8059af517c0c61bddbdd40e06b70acf0b366"}""" },
        { "R19_order_code_int64_max", "reject", """{"code":"00","desc":"success","success":true,"data":{"accountNumber":"0123456789","amount":2000,"code":"00","counterAccountBankId":"970422","counterAccountBankName":"MBBANK","counterAccountName":"CONG TY TNHH A&B","counterAccountNumber":"9876543210","currency":"VND","desc":"success","description":"thanh toan don 100001","orderCode":9223372036854775807,"paymentLinkId":"3f2b9c1e8d7a4b6c9e0f1a2b3c4d5e6f","reference":"FT26303123456","transactionDateTime":"2026-10-30 10:00:00","virtualAccountName":"","virtualAccountNumber":""},"signature":"86d1727602136316b160754273a51c23db1bcca07ecbf73b44ea82ab4cad9885"}""" },
        { "R20_key_trailing_newline", "reject", """{"code":"00","desc":"success","success":true,"data":{"accountNumber":"0123456789","amount":2000,"code":"00","counterAccountBankId":"970422","counterAccountBankName":"MBBANK","counterAccountName":"CONG TY TNHH A&B","counterAccountNumber":"9876543210","currency":"VND","desc":"success","description":"thanh toan don 100001","orderCode":100001,"paymentLinkId":"3f2b9c1e8d7a4b6c9e0f1a2b3c4d5e6f","reference":"FT26303123456","transactionDateTime":"2026-10-30 10:00:00","virtualAccountName":"","virtualAccountNumber":"","newField\n":"x"},"signature":"5a6094e577147b95659090f30b652956800564ae1d81d8c89701f6508cce84d2"}""" },
    };

    /// <summary>payOS's published vectors: valid ones are accepted (their order is not ours), every forged or malformed one is refused.</summary>
    [Theory]
    [MemberData(nameof(Vectors))]
    public async Task The_webhook_signature_check_agrees_with_payOS_test_vectors(string id, string expect, string body)
    {
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        var response = await F.CreateClient().PostAsync(Webhook, content);

        var accepted = response.StatusCode == HttpStatusCode.OK;
        accepted.Should().Be(expect == "accept", id);
    }

    [Fact]
    public async Task The_browser_return_reads_the_status_from_payOS_and_redirects_to_the_result_page()
    {
        var order = Guid.NewGuid();
        var payment = await StartAsync(order, 74_000);
        var code = OrderCodeOf(payment);
        Api.Answer = _ => new { code = "00", desc = "success", data = new { id = "plink1", status = "PAID", amount = 74_000, amountPaid = 74_000, transactions = new[] { new { reference = "FT999", amount = 74_000 } } } };
        var browser = F.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        // The query of the return link is not signed: "status=PAID" there proves nothing, the answer of payOS does.
        var back = await browser.GetAsync($"{Return}?code=00&id=plink1&cancel=false&status=PAID&orderCode={code}");

        back.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var location = back.Headers.Location!.ToString();
        location.Should().StartWith("http://localhost:8443/thanh-toan/ket-qua?");
        var result = QueryHelpers.ParseQuery(new Uri(location).Query);
        (result["status"].ToString(), result["paymentId"].ToString(), result["orderId"].ToString()).Should().Be(("paid", payment.Id().ToString(), order.ToString()));
        Api.Calls.Last().Path.Should().Be($"/v2/payment-requests/{code}");
        (await ReadAsync(payment)).GetProperty("status").GetString().Should().Be("Succeeded");
    }

    [Fact]
    public async Task A_return_that_payOS_does_not_confirm_stays_pending_and_a_cancelled_one_fails()
    {
        var payment = await StartAsync(amount: 74_000);
        var code = OrderCodeOf(payment);
        var browser = F.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var claimed = await browser.GetAsync($"{Return}?code=00&cancel=false&status=PAID&orderCode={code}");   // payOS still says PENDING
        QueryHelpers.ParseQuery(new Uri(claimed.Headers.Location!.ToString()).Query)["status"].ToString().Should().Be("pending");
        (await ReadAsync(payment)).GetProperty("status").GetString().Should().Be("Pending");

        Api.Answer = _ => new { code = "00", desc = "success", data = new { status = "CANCELLED", amountPaid = 0 } };
        var cancelled = await browser.GetAsync($"{Return}?code=00&cancel=true&status=CANCELLED&orderCode={code}");
        var result = QueryHelpers.ParseQuery(new Uri(cancelled.Headers.Location!.ToString()).Query);
        (result["status"].ToString(), result["reason"].ToString()).Should().Be(("failed", "The customer cancelled the payment on payOS."));

        var unknown = await browser.GetAsync($"{Return}?orderCode=42");
        QueryHelpers.ParseQuery(new Uri(unknown.Headers.Location!.ToString()).Query)["status"].ToString().Should().Be("invalid");
    }

    [Fact]
    public async Task A_refund_through_payOS_fails_with_a_clear_reason()
    {
        var payment = await StartAsync(amount: 74_000);
        await HookAsync(Signed(OrderCodeOf(payment), 74_000));

        var refund = await Cashier1.PostOk($"{Tx}/{payment.Id()}/refunds", new { kind = "Cancellation", reason = "Wrong order" });

        refund.GetProperty("status").GetString().Should().Be("Failed");
        (await ReadAsync(payment)).GetProperty("status").GetString().Should().Be("Succeeded");
    }

    [Fact]
    public async Task A_brand_turns_payOS_on_without_an_account_of_its_own()
    {
        var methods = await GetAsync(Customer, $"{Merchants}/available?branchId={B1}");

        methods.EnumerateArray().Select(m => m.GetProperty("providerCode").GetString()).Should().Contain("PayOS");
    }
}
