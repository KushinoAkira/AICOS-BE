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
/// Where payOS lives and where it sends the customer back (section <c>Payments:PayOs</c>). The client id, API key and checksum key are the
/// credentials of the PayOS provider row (merchant id, API key, secret key; set by the platform admin, stored encrypted);
/// <see cref="ClientId"/>, <see cref="ApiKey"/> and <see cref="ChecksumKey"/> here only seed that row on a fresh database.
/// </summary>
public sealed class PayOsOptions
{
    public const string SectionName = "Payments:PayOs";

    public string ApiUrl { get; set; } = "https://api-merchant.payos.vn";

    /// <summary>Where payOS sends the browser after paying or cancelling: the return endpoint of this service, reached through the gateway.</summary>
    public string ReturnUrl { get; set; } = "http://localhost:8080/api/v1/payments/payos/return";

    /// <summary>The web page that shows the result; the return endpoint redirects there with paymentId, orderId and status.</summary>
    public string ResultPageUrl { get; set; } = "http://localhost:8443/thanh-toan/ket-qua";

    public string? ClientId { get; set; }

    public string? ApiKey { get; set; }

    public string? ChecksumKey { get; set; }
}

/// <summary>The keys of the platform's payOS payment channel.</summary>
public sealed record PayOsCredentials(string ClientId, string ApiKey, string ChecksumKey)
{
    /// <summary>Null when the PayOS provider was never set up or misses one of its keys.</summary>
    public static async Task<PayOsCredentials?> LoadAsync(PaymentsDbContext db, SecretProtector protector, CancellationToken ct)
    {
        var provider = await db.PaymentProviders.AsNoTracking().FirstOrDefaultAsync(p => p.Code == PaymentProvider.PayOsCode, ct);
        if (string.IsNullOrWhiteSpace(provider?.MerchantId) || provider.ApiKeyEncrypted is null || provider.SecretKeyEncrypted is null)
        {
            return null;
        }

        return new PayOsCredentials(provider.MerchantId, protector.Unprotect(provider.ApiKeyEncrypted), protector.Unprotect(provider.SecretKeyEncrypted));
    }
}

/// <summary>
/// payOS names a payment by a whole number (orderCode, at most 2^53 - 1). Ours is the first 13 hex digits of the transaction reference
/// ("AICOS" + hex), which fit in 52 bits, so the payment is found again from the orderCode without storing anything new.
/// </summary>
public static class PayOs
{
    private const string Prefix = "AICOS";
    private const int Digits = 13;
    private const long MaxOrderCode = (1L << (Digits * 4)) - 1;

    public static long OrderCodeOf(string transactionRef) =>
        long.Parse(transactionRef.AsSpan(Prefix.Length, Digits), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);

    /// <summary>The start of the transaction reference an orderCode came from; null for a number we never handed out.</summary>
    public static string? TransactionRefPrefixOf(long orderCode) =>
        orderCode is > 0 and <= MaxOrderCode ? Prefix + orderCode.ToString("X" + Digits, CultureInfo.InvariantCulture) : null;

    /// <summary>The transfer content the customer sees: short, because some banks keep only 9 characters.</summary>
    public static string Description(string transactionRef) => "AICOS" + transactionRef[^4..];

    public static long UnixSeconds(DateTime utc) => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeSeconds();
}

/// <summary>
/// payOS signatures: HMAC-SHA256 (hex) with the checksum key. A payment request is signed over amount, cancelUrl, description, orderCode
/// and returnUrl; a webhook over every field of its data, sorted by name, joined as key=value with &amp;. Values are not encoded, so
/// (like payOS's own libraries) data that could shift the delimiters is refused before the signature is checked.
/// </summary>
public static class PayOsSignature
{
    private const long MaxSafeInteger = 9_007_199_254_740_991;
    private static readonly string[] CoreIntegers = ["orderCode", "amount"];
    private static readonly string[] CoreStrings = ["description", "accountNumber", "reference", "transactionDateTime", "currency", "paymentLinkId", "code", "desc"];
    private static readonly string[] OptionalStrings = ["counterAccountBankId", "counterAccountBankName", "counterAccountName", "counterAccountNumber", "virtualAccountName", "virtualAccountNumber"];

    public static string Hmac(string key, string data) =>
        Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(data))).ToLowerInvariant();

    public static string OfPaymentRequest(string checksumKey, long amount, string cancelUrl, string description, long orderCode, string returnUrl) =>
        Hmac(checksumKey, string.Create(CultureInfo.InvariantCulture, $"amount={amount}&cancelUrl={cancelUrl}&description={description}&orderCode={orderCode}&returnUrl={returnUrl}"));

    /// <summary>The string a webhook's data is signed over, or null when the data has a shape payOS never sends.</summary>
    public static string? CanonicalWebhookData(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var fields = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var field in data.EnumerateObject())
        {
            if (!IsValidKey(field.Name) || fields.ContainsKey(field.Name))
            {
                return null;
            }

            string text;
            switch (field.Value.ValueKind)
            {
                case JsonValueKind.Null:
                    text = string.Empty;
                    break;
                case JsonValueKind.String:
                    text = field.Value.GetString()!;
                    if (text.Contains('=', StringComparison.Ordinal))
                    {
                        return null;
                    }

                    break;
                case JsonValueKind.Number when IsSafeInteger(field.Value, out var number):
                    text = number.ToString(CultureInfo.InvariantCulture);
                    break;
                default:
                    return null;
            }

            fields[field.Name] = text;
        }

        if (CoreIntegers.Any(k => !data.TryGetProperty(k, out var v) || v.ValueKind != JsonValueKind.Number)
            || CoreStrings.Any(k => !data.TryGetProperty(k, out var v) || v.ValueKind != JsonValueKind.String)
            || OptionalStrings.Any(k => data.TryGetProperty(k, out var v) && v.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)))
        {
            return null;
        }

        return string.Join("&", fields.Select(f => $"{f.Key}={f.Value}"));
    }

    /// <summary>Whether a webhook body (<c>{ code, desc, success, data, signature }</c>) was signed with this checksum key.</summary>
    public static bool VerifyWebhook(string checksumKey, JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object
            || !body.TryGetProperty("data", out var data)
            || !body.TryGetProperty("signature", out var signature)
            || signature.ValueKind != JsonValueKind.String
            || CanonicalWebhookData(data) is not { } canonical)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(Hmac(checksumKey, canonical)), Encoding.UTF8.GetBytes(signature.GetString() ?? string.Empty));
    }

    private static bool IsValidKey(string key) =>
        key.Length > 0 && key != "__proto__" && key.All(c => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_');

    private static bool IsSafeInteger(JsonElement value, out long number) =>
        value.TryGetInt64(out number) && number is >= -MaxSafeInteger and <= MaxSafeInteger;
}

/// <summary>
/// payOS: the customer pays by bank transfer (VietQR) on payOS's checkout page, into the bank account linked to the platform's payOS
/// channel. The result comes back through the webhook and the browser return (see <c>PayOsEndpoints</c>); the status query reads the
/// payment link from payOS. payOS has no refund API: a refund has to be transferred back by hand.
/// </summary>
public sealed class PayOsGateway(
    IServiceScopeFactory scopes, SecretProtector protector, IHttpClientFactory http, IOptions<PayOsOptions> options, TimeProvider clock, ILogger<PayOsGateway> logger)
    : IPaymentGateway
{
    public const string HttpClientName = "payos";

    public string Code => PaymentProvider.PayOsCode;

    public async Task<GatewayCheckout> CreatePaymentAsync(GatewayPaymentRequest r, CancellationToken ct)
    {
        var credentials = await CredentialsAsync(ct)
            ?? throw new BusinessRuleException("payment.payos_not_configured", "payOS has no client id, API key or checksum key yet.");
        var orderCode = PayOs.OrderCodeOf(r.TransactionRef);
        var amount = decimal.ToInt64(r.Amount);
        var description = PayOs.Description(r.TransactionRef);
        var returnUrl = options.Value.ReturnUrl;
        var expiresAt = r.ExpiresAtUtc ?? clock.GetUtcNow().UtcDateTime.AddMinutes(15);
        var body = new
        {
            orderCode,
            amount,
            description,
            cancelUrl = returnUrl,
            returnUrl,
            expiredAt = PayOs.UnixSeconds(expiresAt),
            signature = PayOsSignature.OfPaymentRequest(credentials.ChecksumKey, amount, returnUrl, description, orderCode, returnUrl),
        };

        var answer = await SendAsync(credentials, HttpMethod.Post, "/v2/payment-requests", body, ct);
        if (answer is not { } json || Text(json, "code") != "00" || !json.TryGetProperty("data", out var data) || Text(data, "checkoutUrl") is not { Length: > 0 } checkoutUrl)
        {
            var reason = answer is { } failed ? $"{Text(failed, "code")} {Text(failed, "desc")}".Trim() : "no answer";
            throw new BusinessRuleException("payment.payos_failed", $"payOS did not create the payment link ({reason}).");
        }

        return new GatewayCheckout(checkoutUrl);
    }

    /// <summary>Reads the payment link. Anything unclear (no answer, still waiting) is Pending, so it never fails a payment by mistake.</summary>
    public async Task<GatewayStatus> QueryAsync(Payment payment, CancellationToken ct)
    {
        var pending = new GatewayStatus(ProviderState.Pending, null, null);
        var credentials = await CredentialsAsync(ct);
        if (credentials is null)
        {
            return pending;
        }

        var answer = await SendAsync(credentials, HttpMethod.Get, $"/v2/payment-requests/{PayOs.OrderCodeOf(payment.TransactionRef)}", null, ct);
        if (answer is not { } json || Text(json, "code") != "00" || !json.TryGetProperty("data", out var data))
        {
            return pending;
        }

        switch (Text(data, "status"))
        {
            case "PAID" when data.TryGetProperty("amountPaid", out var paid) && paid.TryGetInt64(out var amountPaid) && amountPaid >= decimal.ToInt64(payment.Amount):
                var reference = data.TryGetProperty("transactions", out var transactions) && transactions.ValueKind == JsonValueKind.Array && transactions.GetArrayLength() > 0
                    ? Text(transactions[transactions.GetArrayLength() - 1], "reference")
                    : null;
                return new GatewayStatus(ProviderState.Succeeded, string.IsNullOrEmpty(reference) ? Text(data, "id") : reference, null);
            case "CANCELLED":
                return new GatewayStatus(ProviderState.Failed, null, "The customer cancelled the payment on payOS.");
            case "EXPIRED":
                return new GatewayStatus(ProviderState.Failed, null, "The payOS payment link expired.");
            case "FAILED":
                return new GatewayStatus(ProviderState.Failed, null, "payOS reports the payment as failed.");
            default:
                return pending;   // PENDING, PROCESSING, UNDERPAID (the customer sent less: reconciliation shows it)
        }
    }

    public Task<GatewayRefundResult> RefundAsync(Payment payment, decimal amount, string reason, CancellationToken ct) =>
        Task.FromResult(new GatewayRefundResult(false, null, "payOS has no refund API: transfer the money back to the customer by hand."));

    private async Task<PayOsCredentials?> CredentialsAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        return await PayOsCredentials.LoadAsync(scope.ServiceProvider.GetRequiredService<PaymentsDbContext>(), protector, ct);
    }

    /// <summary>Calls payOS with the channel's keys. The answer comes over HTTPS from payOS itself, so it is read without checking its own signature.</summary>
    private async Task<JsonElement?> SendAsync(PayOsCredentials credentials, HttpMethod method, string path, object? body, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(method, options.Value.ApiUrl.TrimEnd('/') + path);
            request.Headers.Add("x-client-id", credentials.ClientId);
            request.Headers.Add("x-api-key", credentials.ApiKey);
            if (body is not null)
            {
                request.Content = JsonContent.Create(body);
            }

            using var response = await http.CreateClient(HttpClientName).SendAsync(request, ct);
            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            return document.RootElement.Clone();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            logger.LogWarning(ex, "Calling payOS ({Method} {Path}) failed.", method, path);
            return null;
        }
    }

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
            ? value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText()
            : null;
}
