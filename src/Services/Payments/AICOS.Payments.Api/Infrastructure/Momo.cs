using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AICOS.BuildingBlocks.Exceptions;
using AICOS.Payments.Api.Domain;
using AICOS.Payments.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AICOS.Payments.Api.Infrastructure;

/// <summary>
/// Where MoMo lives and where it sends the customer back (section <c>Payments:Momo</c>). The partner code, access key and secret key are the
/// credentials of the Momo provider row (merchant id, API key, secret key; stored encrypted); <see cref="PartnerCode"/>,
/// <see cref="AccessKey"/> and <see cref="SecretKey"/> here only seed that row. <see cref="Live"/> turns the real MoMo calls on (it is on as
/// soon as a partner code is configured); off, Momo stays the simulated provider used by the demo and the tests.
/// </summary>
public sealed class MomoOptions
{
    public const string SectionName = "Payments:Momo";

    /// <summary>The test environment by default; https://payment.momo.vn for production.</summary>
    public string ApiUrl { get; set; } = "https://test-payment.momo.vn";

    public bool Live { get; set; }

    /// <summary><c>payWithMethod</c> lets the customer pick the MoMo wallet, an ATM card or a credit card on MoMo's page; <c>captureWallet</c> only the wallet.</summary>
    public string RequestType { get; set; } = "payWithMethod";

    /// <summary>Where MoMo sends the browser after paying or cancelling: the return endpoint of this service, reached through the gateway.</summary>
    public string ReturnUrl { get; set; } = "http://localhost:8080/api/v1/payments/momo/return";

    /// <summary>Where MoMo posts the result (IPN). MoMo cannot reach localhost; the browser return and the status query cover that case.</summary>
    public string IpnUrl { get; set; } = "http://localhost:8080/api/v1/payments/momo/ipn";

    public string ResultPageUrl { get; set; } = "http://localhost:8443/thanh-toan/ket-qua";

    public string? PartnerCode { get; set; }

    public string? AccessKey { get; set; }

    public string? SecretKey { get; set; }
}

/// <summary>The keys of the platform's MoMo partner account.</summary>
public sealed record MomoCredentials(string PartnerCode, string AccessKey, string SecretKey)
{
    /// <summary>Null when the Momo provider was never set up or misses one of its keys.</summary>
    public static async Task<MomoCredentials?> LoadAsync(PaymentsDbContext db, SecretProtector protector, CancellationToken ct)
    {
        var provider = await db.PaymentProviders.AsNoTracking().FirstOrDefaultAsync(p => p.Code == PaymentProvider.MomoCode, ct);
        if (string.IsNullOrWhiteSpace(provider?.MerchantId) || provider.ApiKeyEncrypted is null || provider.SecretKeyEncrypted is null)
        {
            return null;
        }

        return new MomoCredentials(provider.MerchantId, protector.Unprotect(provider.ApiKeyEncrypted), protector.Unprotect(provider.SecretKeyEncrypted));
    }
}

/// <summary>
/// MoMo signatures: HMAC-SHA256 (hex) with the secret key over <c>key=value</c> pairs joined with &amp;, keys in the order MoMo documents
/// (alphabetical). Values are not encoded.
/// </summary>
public static class MomoSignature
{
    /// <summary>The fields an IPN is signed over, in order.</summary>
    public static readonly string[] IpnFields = ["amount", "extraData", "message", "orderId", "orderInfo", "orderType", "partnerCode", "payType", "requestId", "responseTime", "resultCode", "transId"];

    public static string Hmac(string secretKey, string data) =>
        Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secretKey), Encoding.UTF8.GetBytes(data))).ToLowerInvariant();

    public static string Sign(MomoCredentials c, params (string Key, object Value)[] fields) =>
        Hmac(c.SecretKey, "accessKey=" + c.AccessKey + string.Concat(fields.Select(f => "&" + f.Key + "=" + Convert.ToString(f.Value, CultureInfo.InvariantCulture))));

    /// <summary>A field of a MoMo body as text (numbers as MoMo wrote them); null when it is missing or not a plain value.</summary>
    public static string? Text(JsonElement body, string name) =>
        body.ValueKind == JsonValueKind.Object && body.TryGetProperty(name, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.GetRawText(),
                JsonValueKind.Null => string.Empty,
                _ => null,
            }
            : null;

    /// <summary>Whether an IPN body was signed with this account's secret key (and is for this partner).</summary>
    public static bool VerifyIpn(MomoCredentials c, JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object || Text(body, "signature") is not { Length: > 0 } signature || Text(body, "partnerCode") != c.PartnerCode)
        {
            return false;
        }

        var parts = new List<(string, object)>();
        foreach (var field in IpnFields)
        {
            if (Text(body, field) is not { } value)
            {
                return false;
            }

            parts.Add((field, value));
        }

        var expected = Sign(c, [.. parts]);
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(signature.Trim().ToLowerInvariant()));
    }

    /// <summary>What a MoMo result code means for a payment. Codes below 1000 other than 0 are request or system errors: unknown, so pending.</summary>
    public static ProviderState StateOf(int resultCode) => resultCode switch
    {
        0 or 9000 => ProviderState.Succeeded,
        1000 or 7000 or 7002 => ProviderState.Pending,
        > 1000 => ProviderState.Failed,
        _ => ProviderState.Pending,
    };
}

/// <summary>
/// MoMo (all-in-one payment gateway): the customer pays on MoMo's page with the MoMo wallet, an ATM card or a credit card. The result comes
/// back through the IPN and the browser return (see <c>MomoEndpoints</c>); the status query asks MoMo; refunds go back through MoMo's API.
/// Until <see cref="MomoOptions.Live"/> is on, Momo is the simulated provider, as before.
/// </summary>
public sealed class MomoGateway(
    IServiceScopeFactory scopes, SecretProtector protector, IHttpClientFactory http, IOptions<MomoOptions> options, ILogger<MomoGateway> logger)
    : IPaymentGateway
{
    public const string HttpClientName = "momo";
    private readonly SimulatedGateway _simulated = new(PaymentProvider.MomoCode);

    public string Code => PaymentProvider.MomoCode;

    public async Task<GatewayCheckout> CreatePaymentAsync(GatewayPaymentRequest r, CancellationToken ct)
    {
        if (!options.Value.Live)
        {
            return await _simulated.CreatePaymentAsync(r, ct);
        }

        var c = await CredentialsAsync(ct) ?? throw new BusinessRuleException("payment.momo_not_configured", "MoMo has no partner code, access key or secret key yet.");
        var o = options.Value;
        var amount = decimal.ToInt64(r.Amount);
        var orderInfo = $"Thanh toan don AICOS {r.TransactionRef[^6..]}";
        const string extraData = "";
        var body = new
        {
            partnerCode = c.PartnerCode,
            requestId = r.TransactionRef,
            amount,
            orderId = r.TransactionRef,
            orderInfo,
            redirectUrl = o.ReturnUrl,
            ipnUrl = o.IpnUrl,
            requestType = o.RequestType,
            extraData,
            autoCapture = true,
            lang = "vi",
            signature = MomoSignature.Sign(c, ("amount", amount), ("extraData", extraData), ("ipnUrl", o.IpnUrl), ("orderId", r.TransactionRef), ("orderInfo", orderInfo),
                ("partnerCode", c.PartnerCode), ("redirectUrl", o.ReturnUrl), ("requestId", r.TransactionRef), ("requestType", o.RequestType)),
        };

        var answer = await PostAsync("/v2/gateway/api/create", body, ct);
        if (answer is not { } json || ResultCode(json) != 0 || MomoSignature.Text(json, "payUrl") is not { Length: > 0 } payUrl)
        {
            var reason = answer is { } failed ? $"{MomoSignature.Text(failed, "resultCode")} {MomoSignature.Text(failed, "message")}".Trim() : "no answer";
            throw new BusinessRuleException("payment.momo_failed", $"MoMo did not create the payment ({reason}).");
        }

        return new GatewayCheckout(payUrl);
    }

    /// <summary>Asks MoMo about the order. Anything unclear (no answer, a request error, still waiting) is Pending, so it never fails a payment by mistake.</summary>
    public async Task<GatewayStatus> QueryAsync(Payment payment, CancellationToken ct)
    {
        if (!options.Value.Live)
        {
            return await _simulated.QueryAsync(payment, ct);
        }

        var pending = new GatewayStatus(ProviderState.Pending, null, null);
        var c = await CredentialsAsync(ct);
        if (c is null)
        {
            return pending;
        }

        var requestId = "Q" + Guid.NewGuid().ToString("N")[..20].ToUpperInvariant();
        var answer = await PostAsync("/v2/gateway/api/query", new
        {
            partnerCode = c.PartnerCode,
            requestId,
            orderId = payment.TransactionRef,
            lang = "vi",
            signature = MomoSignature.Sign(c, ("orderId", payment.TransactionRef), ("partnerCode", c.PartnerCode), ("requestId", requestId)),
        }, ct);
        if (answer is not { } json || ResultCode(json) is not { } code)
        {
            return pending;
        }

        switch (MomoSignature.StateOf(code))
        {
            case ProviderState.Succeeded when long.TryParse(MomoSignature.Text(json, "amount"), out var paid) && paid >= decimal.ToInt64(payment.Amount):
                return new GatewayStatus(ProviderState.Succeeded, MomoSignature.Text(json, "transId"), null);
            case ProviderState.Failed:
                return new GatewayStatus(ProviderState.Failed, null, $"MoMo: {MomoSignature.Text(json, "message")} ({code}).");
            default:
                return pending;
        }
    }

    /// <summary>Refunds through MoMo, against the MoMo transaction of the payment (its provider reference).</summary>
    public async Task<GatewayRefundResult> RefundAsync(Payment payment, decimal amount, string reason, CancellationToken ct)
    {
        if (!options.Value.Live)
        {
            return await _simulated.RefundAsync(payment, amount, reason, ct);
        }

        if (!long.TryParse(payment.ProviderReference, NumberStyles.None, CultureInfo.InvariantCulture, out var transId))
        {
            return new GatewayRefundResult(false, null, "The payment has no MoMo transaction id to refund.");
        }

        var c = await CredentialsAsync(ct);
        if (c is null)
        {
            return new GatewayRefundResult(false, null, "MoMo has no partner code, access key or secret key yet.");
        }

        var value = decimal.ToInt64(amount);
        var orderId = "RF" + Guid.NewGuid().ToString("N")[..20].ToUpperInvariant();
        var description = $"Hoan tien don AICOS {payment.TransactionRef[^6..]}";
        var answer = await PostAsync("/v2/gateway/api/refund", new
        {
            partnerCode = c.PartnerCode,
            orderId,
            requestId = orderId,
            amount = value,
            transId,
            lang = "vi",
            description,
            signature = MomoSignature.Sign(c, ("amount", value), ("description", description), ("orderId", orderId), ("partnerCode", c.PartnerCode), ("requestId", orderId), ("transId", transId)),
        }, ct);

        if (answer is { } json && ResultCode(json) == 0)
        {
            return new GatewayRefundResult(true, MomoSignature.Text(json, "transId") ?? orderId, null);
        }

        var why = answer is { } failed ? $"{MomoSignature.Text(failed, "message")} ({MomoSignature.Text(failed, "resultCode")})" : "no answer";
        return new GatewayRefundResult(false, null, $"MoMo refused the refund: {why}.");
    }

    private async Task<MomoCredentials?> CredentialsAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        return await MomoCredentials.LoadAsync(scope.ServiceProvider.GetRequiredService<PaymentsDbContext>(), protector, ct);
    }

    private static int? ResultCode(JsonElement json) =>
        int.TryParse(MomoSignature.Text(json, "resultCode"), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var code) ? code : null;

    /// <summary>Calls MoMo. The answer comes over HTTPS from MoMo itself, so it is read without checking its own signature.</summary>
    private async Task<JsonElement?> PostAsync(string path, object body, CancellationToken ct)
    {
        try
        {
            using var response = await http.CreateClient(HttpClientName).PostAsJsonAsync(options.Value.ApiUrl.TrimEnd('/') + path, body, ct);
            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            return document.RootElement.Clone();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            logger.LogWarning(ex, "Calling MoMo ({Path}) failed.", path);
            return null;
        }
    }
}
