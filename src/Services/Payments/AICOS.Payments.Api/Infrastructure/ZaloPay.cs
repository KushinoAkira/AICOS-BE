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
/// Where ZaloPay lives and where it sends the customer back (section <c>Payments:ZaloPay</c>). The app id, key1 and key2 are the
/// credentials of the ZaloPay provider row (merchant id, API key, secret key; stored encrypted); <see cref="AppId"/>, <see cref="Key1"/>
/// and <see cref="Key2"/> here only seed that row. <see cref="Live"/> turns the real ZaloPay calls on (it is on as soon as an app id is
/// configured); off, ZaloPay stays the simulated provider used by the demo and the tests.
/// </summary>
public sealed class ZaloPayOptions
{
    public const string SectionName = "Payments:ZaloPay";

    /// <summary>The sandbox by default; https://openapi.zalopay.vn for production.</summary>
    public string ApiUrl { get; set; } = "https://sb-openapi.zalopay.vn";

    public bool Live { get; set; }

    /// <summary>Empty: ZaloPay's page offers every method (ZaloPay app, ATM card, credit card). <c>zalopayapp</c> or <c>CC</c> narrow it.</summary>
    public string BankCode { get; set; } = string.Empty;

    /// <summary>Where ZaloPay sends the browser after paying (embed_data.redirecturl): the return endpoint of this service, through the gateway.</summary>
    public string ReturnUrl { get; set; } = "http://localhost:8080/api/v1/payments/zalopay/return";

    /// <summary>Where ZaloPay posts the result. ZaloPay cannot reach localhost; the browser return and the status query cover that case.</summary>
    public string CallbackUrl { get; set; } = "http://localhost:8080/api/v1/payments/zalopay/callback";

    public string ResultPageUrl { get; set; } = "http://localhost:8443/thanh-toan/ket-qua";

    public string? AppId { get; set; }

    public string? Key1 { get; set; }

    public string? Key2 { get; set; }

    /// <summary>
    /// For testing only. ZaloPay's public sandbox apps let every test card through, so the failures a customer can meet cannot be played
    /// on them. Set to <c>lost</c>, <c>no_money</c>, <c>timeout</c> (or anything else for a generic refusal), every payment ZaloPay
    /// reports as paid is taken as refused for that reason instead. Empty (the default): ZaloPay's answer is taken as it is.
    /// </summary>
    public string SimulateFailure { get; set; } = string.Empty;
}

/// <summary>The keys of the platform's ZaloPay merchant app: key1 signs the requests, key2 the callbacks.</summary>
public sealed record ZaloPayCredentials(string AppId, string Key1, string Key2)
{
    public static async Task<ZaloPayCredentials?> LoadAsync(PaymentsDbContext db, SecretProtector protector, CancellationToken ct)
    {
        var provider = await db.PaymentProviders.AsNoTracking().FirstOrDefaultAsync(p => p.Code == ZaloPay.Code, ct);
        if (string.IsNullOrWhiteSpace(provider?.MerchantId) || provider.ApiKeyEncrypted is null || provider.SecretKeyEncrypted is null)
        {
            return null;
        }

        return new ZaloPayCredentials(provider.MerchantId, protector.Unprotect(provider.ApiKeyEncrypted), protector.Unprotect(provider.SecretKeyEncrypted));
    }
}

/// <summary>
/// ZaloPay names an order <c>yymmdd_...</c> with the date in Vietnam time. Ours is that date plus the transaction reference, so the payment is
/// found again from the app_trans_id (the part after the underscore) without storing anything new.
/// </summary>
public static class ZaloPay
{
    public const string Code = "ZaloPay";

    private static readonly TimeSpan Vietnam = TimeSpan.FromHours(7);

    public static string AppTransId(DateTime utc, string transactionRef) =>
        (DateTime.SpecifyKind(utc, DateTimeKind.Utc) + Vietnam).ToString("yyMMdd", CultureInfo.InvariantCulture) + "_" + transactionRef;

    /// <summary>The transaction reference inside an app_trans_id; null when it is not one of ours.</summary>
    public static string? TransactionRefOf(string? appTransId) =>
        appTransId is { Length: > 7 } && appTransId[6] == '_' ? appTransId[7..] : null;

    public static long UnixMilliseconds(DateTime utc) => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();

    public static string Hmac(string key, string data) =>
        Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(data))).ToLowerInvariant();

    /// <summary>Whether a callback (<c>{ data, mac, type }</c>) was signed with key2: the mac is HMAC-SHA256 of the data string as sent.</summary>
    public static bool VerifyCallback(string key2, JsonElement body, out JsonElement data)
    {
        data = default;
        if (body.ValueKind != JsonValueKind.Object
            || !body.TryGetProperty("data", out var raw) || raw.ValueKind != JsonValueKind.String
            || !body.TryGetProperty("mac", out var mac) || mac.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var text = raw.GetString()!;
        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(Hmac(key2, text)), Encoding.UTF8.GetBytes(mac.GetString()!.Trim().ToLowerInvariant())))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            data = document.RootElement.Clone();
            return data.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>A field of a ZaloPay body as text (numbers as written); null when missing.</summary>
    public static string? Text(JsonElement body, string name) =>
        body.ValueKind == JsonValueKind.Object && body.TryGetProperty(name, out var value)
            ? value.ValueKind == JsonValueKind.String ? value.GetString() : value.ValueKind == JsonValueKind.Number ? value.GetRawText() : null
            : null;

    /// <summary>The refusal played instead of a payment when <see cref="ZaloPayOptions.SimulateFailure"/> is set; null when it is not.</summary>
    public static string? SimulatedFailure(string? setting) => setting?.Trim().ToLowerInvariant() switch
    {
        null or "" => null,
        "lost" => "ZaloPay (giả lập): Thẻ bị mất hoặc bị khóa (-217).",
        "no_money" => "ZaloPay (giả lập): Số dư tài khoản không đủ (-63).",
        "timeout" => "ZaloPay (giả lập): Giao dịch quá thời gian chờ (-54).",
        _ => "ZaloPay (giả lập): Giao dịch thất bại (-1).",
    };

    public static int? ReturnCode(JsonElement body) =>
        int.TryParse(Text(body, "return_code"), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var code) ? code : null;
}

/// <summary>
/// ZaloPay (payment gateway): the customer pays on ZaloPay's page with the ZaloPay app, an ATM card or a credit card. The result comes back
/// through the callback and the browser return (see <c>ZaloPayEndpoints</c>); the status query asks ZaloPay; refunds go back through ZaloPay's
/// API. Until <see cref="ZaloPayOptions.Live"/> is on, ZaloPay is the simulated provider, as before.
/// </summary>
public sealed class ZaloPayGateway(
    IServiceScopeFactory scopes, SecretProtector protector, IHttpClientFactory http, IOptions<ZaloPayOptions> options, TimeProvider clock, ILogger<ZaloPayGateway> logger)
    : IPaymentGateway
{
    public const string HttpClientName = "zalopay";
    private readonly SimulatedGateway _simulated = new(ZaloPay.Code);

    public string Code => ZaloPay.Code;

    public async Task<GatewayCheckout> CreatePaymentAsync(GatewayPaymentRequest r, CancellationToken ct)
    {
        if (!options.Value.Live)
        {
            return await _simulated.CreatePaymentAsync(r, ct);
        }

        var c = await CredentialsAsync(ct) ?? throw new BusinessRuleException("payment.zalopay_not_configured", "ZaloPay has no app id, key1 or key2 yet.");
        var o = options.Value;
        var now = clock.GetUtcNow().UtcDateTime;
        var appTransId = ZaloPay.AppTransId(now, r.TransactionRef);
        var amount = decimal.ToInt64(r.Amount).ToString(CultureInfo.InvariantCulture);
        var appTime = ZaloPay.UnixMilliseconds(now).ToString(CultureInfo.InvariantCulture);
        const string appUser = "AICOS", item = "[]";
        var embedData = JsonSerializer.Serialize(new { redirecturl = o.ReturnUrl });
        var fields = new Dictionary<string, string>
        {
            ["app_id"] = c.AppId,
            ["app_user"] = appUser,
            ["app_trans_id"] = appTransId,
            ["app_time"] = appTime,
            ["amount"] = amount,
            ["item"] = item,
            ["embed_data"] = embedData,
            ["description"] = $"AICOS - Thanh toan don {r.TransactionRef[^6..]}",
            ["bank_code"] = o.BankCode,
            ["callback_url"] = o.CallbackUrl,
            ["mac"] = ZaloPay.Hmac(c.Key1, $"{c.AppId}|{appTransId}|{appUser}|{amount}|{appTime}|{embedData}|{item}"),
        };
        if (r.ExpiresAtUtc is { } expires)
        {
            fields["expire_duration_seconds"] = Math.Clamp((long)(expires - now).TotalSeconds, 300, 2_592_000).ToString(CultureInfo.InvariantCulture);
        }

        var answer = await PostAsync("/v2/create", fields, ct);
        if (answer is not { } json || ZaloPay.ReturnCode(json) != 1 || ZaloPay.Text(json, "order_url") is not { Length: > 0 } orderUrl)
        {
            var reason = answer is { } failed ? $"{ZaloPay.Text(failed, "sub_return_code") ?? ZaloPay.Text(failed, "return_code")} {ZaloPay.Text(failed, "sub_return_message") ?? ZaloPay.Text(failed, "return_message")}".Trim() : "no answer";
            throw new BusinessRuleException("payment.zalopay_failed", $"ZaloPay did not create the payment ({reason}).");
        }

        return new GatewayCheckout(orderUrl);
    }

    /// <summary>Asks ZaloPay about the order. 1 paid, 2 failed, 3 still processing; anything unclear is Pending.</summary>
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

        var appTransId = ZaloPay.AppTransId(payment.CreatedAtUtc, payment.TransactionRef);
        var answer = await PostAsync("/v2/query", new Dictionary<string, string>
        {
            ["app_id"] = c.AppId,
            ["app_trans_id"] = appTransId,
            ["mac"] = ZaloPay.Hmac(c.Key1, $"{c.AppId}|{appTransId}|{c.Key1}"),
        }, ct);
        if (answer is not { } json)
        {
            return pending;
        }

        if (ZaloPay.ReturnCode(json) == 1 && ZaloPay.SimulatedFailure(options.Value.SimulateFailure) is { } simulated)
        {
            logger.LogWarning("ZaloPay failure simulation is on: payment {Ref} is taken as refused.", payment.TransactionRef);
            return new GatewayStatus(ProviderState.Failed, null, simulated);
        }

        return ZaloPay.ReturnCode(json) switch
        {
            1 when long.TryParse(ZaloPay.Text(json, "amount"), out var paid) && paid >= decimal.ToInt64(payment.Amount) =>
                new GatewayStatus(ProviderState.Succeeded, ZaloPay.Text(json, "zp_trans_id"), null),
            2 => new GatewayStatus(ProviderState.Failed, null, $"ZaloPay: {ZaloPay.Text(json, "sub_return_message") ?? ZaloPay.Text(json, "return_message")} ({ZaloPay.Text(json, "sub_return_code") ?? "2"})."),
            _ => pending,
        };
    }

    /// <summary>Refunds through ZaloPay against the ZaloPay transaction of the payment (its provider reference).</summary>
    public async Task<GatewayRefundResult> RefundAsync(Payment payment, decimal amount, string reason, CancellationToken ct)
    {
        if (!options.Value.Live)
        {
            return await _simulated.RefundAsync(payment, amount, reason, ct);
        }

        if (string.IsNullOrWhiteSpace(payment.ProviderReference) || !payment.ProviderReference.All(char.IsAsciiDigit))
        {
            return new GatewayRefundResult(false, null, "The payment has no ZaloPay transaction id to refund.");
        }

        var c = await CredentialsAsync(ct);
        if (c is null)
        {
            return new GatewayRefundResult(false, null, "ZaloPay has no app id, key1 or key2 yet.");
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var refundId = (now + TimeSpan.FromHours(7)).ToString("yyMMdd", CultureInfo.InvariantCulture) + "_" + c.AppId + "_" + Guid.NewGuid().ToString("N")[..16];
        var value = decimal.ToInt64(amount).ToString(CultureInfo.InvariantCulture);
        var timestamp = ZaloPay.UnixMilliseconds(now).ToString(CultureInfo.InvariantCulture);
        var description = $"AICOS hoan tien don {payment.TransactionRef[^6..]}";
        var answer = await PostAsync("/v2/refund", new Dictionary<string, string>
        {
            ["m_refund_id"] = refundId,
            ["app_id"] = c.AppId,
            ["zp_trans_id"] = payment.ProviderReference,
            ["amount"] = value,
            ["timestamp"] = timestamp,
            ["description"] = description,
            ["mac"] = ZaloPay.Hmac(c.Key1, $"{c.AppId}|{payment.ProviderReference}|{value}|{description}|{timestamp}"),
        }, ct);

        // 1 refunded, 3 being processed by ZaloPay (it completes on its side), 2 refused.
        return answer is { } json && ZaloPay.ReturnCode(json) is 1 or 3
            ? new GatewayRefundResult(true, ZaloPay.Text(json, "refund_id") ?? refundId, null)
            : new GatewayRefundResult(false, null, answer is { } failed
                ? $"ZaloPay refused the refund: {ZaloPay.Text(failed, "sub_return_message") ?? ZaloPay.Text(failed, "return_message")} ({ZaloPay.Text(failed, "sub_return_code") ?? ZaloPay.Text(failed, "return_code")})."
                : "ZaloPay did not answer the refund.");
    }

    private async Task<ZaloPayCredentials?> CredentialsAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        return await ZaloPayCredentials.LoadAsync(scope.ServiceProvider.GetRequiredService<PaymentsDbContext>(), protector, ct);
    }

    /// <summary>Calls ZaloPay with a form body. The answer comes over HTTPS from ZaloPay itself, so it is read as is.</summary>
    private async Task<JsonElement?> PostAsync(string path, Dictionary<string, string> fields, CancellationToken ct)
    {
        try
        {
            using var content = new FormUrlEncodedContent(fields);
            using var response = await http.CreateClient(HttpClientName).PostAsync(options.Value.ApiUrl.TrimEnd('/') + path, content, ct);
            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            return document.RootElement.Clone();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            logger.LogWarning(ex, "Calling ZaloPay ({Path}) failed.", path);
            return null;
        }
    }
}
