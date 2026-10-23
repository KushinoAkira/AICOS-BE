using System.Security.Cryptography;
using System.Text;
using AICOS.Payments.Api.Domain;

namespace AICOS.Payments.Api.Infrastructure;

/// <summary>What a gateway needs to start a payment. Account details come from the merchant setting of the brand or branch.</summary>
public sealed record GatewayPaymentRequest(
    string TransactionRef, decimal Amount, string Description, DateTime? ExpiresAtUtc, string? BankCode, string? AccountNumber, string? AccountName, string? MerchantCode);

public sealed record GatewayCheckout(string CheckoutUrl);

public sealed record GatewayStatus(ProviderState State, string? ProviderReference, string? FailureReason);

public sealed record GatewayRefundResult(bool Success, string? ProviderReference, string? FailureReason);

/// <summary>
/// The one interface every payment provider implements (SCRUM-85). The Payment service talks to providers only through it, so adding a
/// real provider means adding a class here, not touching the payment flow.
/// </summary>
public interface IPaymentGateway
{
    string Code { get; }

    Task<GatewayCheckout> CreatePaymentAsync(GatewayPaymentRequest request, CancellationToken ct);

    /// <summary>Asks the provider what happened to a payment (used when a webhook is lost).</summary>
    Task<GatewayStatus> QueryAsync(Payment payment, CancellationToken ct);

    Task<GatewayRefundResult> RefundAsync(Payment payment, decimal amount, string reason, CancellationToken ct);
}

/// <summary>
/// A provider that runs entirely inside AICOS: no network call, nothing leaves the system. The Mock provider is exactly this; VietQR, Momo
/// and ZaloPay use it too until real sandbox credentials and calls are wired in, and differ only in the checkout link they hand out.
/// What "the provider" knows about a payment is the <see cref="Payment.ProviderState"/> column, which the simulate endpoint sets.
/// </summary>
public sealed class SimulatedGateway(string code) : IPaymentGateway
{
    public string Code => code;

    public Task<GatewayCheckout> CreatePaymentAsync(GatewayPaymentRequest r, CancellationToken ct)
    {
        var amount = decimal.ToInt64(r.Amount);
        var url = code switch
        {
            // VietQR publishes a public image URL for a transfer QR; the amount and transfer content are fixed into it.
            "VietQR" when r.BankCode is not null && r.AccountNumber is not null =>
                $"https://img.vietqr.io/image/{r.BankCode}-{r.AccountNumber}-compact2.png?amount={amount}&addInfo={Uri.EscapeDataString(r.TransactionRef)}&accountName={Uri.EscapeDataString(r.AccountName ?? string.Empty)}",
            // Placeholder links: the sandbox payment page of the wallet is not part of the MVP.
            "Momo" or "ZaloPay" => $"/sandbox/{code.ToLowerInvariant()}/pay/{r.TransactionRef}?amount={amount}",
            _ => $"/sandbox/mock/pay/{r.TransactionRef}?amount={amount}",
        };

        return Task.FromResult(new GatewayCheckout(url));
    }

    public Task<GatewayStatus> QueryAsync(Payment payment, CancellationToken ct) => Task.FromResult(payment.ProviderState switch
    {
        ProviderState.Succeeded => new GatewayStatus(ProviderState.Succeeded, $"{code.ToUpperInvariant()}-{payment.TransactionRef}", null),
        ProviderState.Failed => new GatewayStatus(ProviderState.Failed, null, "Declined by the provider."),
        _ => new GatewayStatus(ProviderState.Pending, null, null),
    });

    public Task<GatewayRefundResult> RefundAsync(Payment payment, decimal amount, string reason, CancellationToken ct) =>
        Task.FromResult(new GatewayRefundResult(true, $"RF-{code.ToUpperInvariant()}-{Guid.NewGuid():N}"[..24], null));
}

/// <summary>Finds the gateway of a provider code.</summary>
public sealed class GatewayRegistry
{
    private readonly Dictionary<string, IPaymentGateway> _gateways;

    public GatewayRegistry(IEnumerable<IPaymentGateway> gateways) =>
        _gateways = gateways.ToDictionary(g => g.Code, StringComparer.OrdinalIgnoreCase);

    public IPaymentGateway For(string code) => _gateways[code];
}

/// <summary>Signs and verifies webhook bodies: HMAC-SHA256 (hex) over <c>eventId|transactionRef|status|amount</c>.</summary>
public static class WebhookSignature
{
    public static string Compute(string secret, string eventId, string transactionRef, string status, decimal amount)
    {
        var data = Encoding.UTF8.GetBytes($"{eventId}|{transactionRef}|{status}|{decimal.ToInt64(amount)}");
        return Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), data)).ToLowerInvariant();
    }

    public static bool Verify(string secret, string eventId, string transactionRef, string status, decimal amount, string? signature)
    {
        if (string.IsNullOrWhiteSpace(signature))
        {
            return false;
        }

        var expected = Encoding.UTF8.GetBytes(Compute(secret, eventId, transactionRef, status, amount));
        return CryptographicOperations.FixedTimeEquals(expected, Encoding.UTF8.GetBytes(signature.Trim().ToLowerInvariant()));
    }
}
