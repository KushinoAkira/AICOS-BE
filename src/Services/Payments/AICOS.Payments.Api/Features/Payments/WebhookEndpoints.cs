using AICOS.BuildingBlocks.Exceptions;
using AICOS.BuildingBlocks.Validation;
using AICOS.Payments.Api.Domain;
using AICOS.Payments.Api.Infrastructure;
using AICOS.Payments.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AICOS.Payments.Api.Features.Payments;

/// <summary>What a provider posts to us. <c>Status</c> is "success" or "failed"; <c>Signature</c> is <see cref="WebhookSignature.Compute"/> of the other fields.</summary>
public sealed record WebhookRequest(string? EventId, string? TransactionRef, string? Status, decimal? Amount, string? ProviderReference, string? Signature);

public sealed record WebhookResponse(string Outcome, bool Duplicate);

/// <summary>
/// Provider webhook (SCRUM-87). Anonymous, because the provider holds no AICOS token; the HMAC signature is the authentication.
/// Idempotent: a retried event id is answered with the first result and changes nothing. Always 200 once the signature is right, so
/// the provider stops retrying; <c>outcome</c> says what we did with it.
/// </summary>
public static class WebhookEndpoints
{
    public static void MapWebhookEndpoints(this RouteGroupBuilder api)
    {
        api.MapPost("/webhooks/{provider}", ReceiveAsync).AllowAnonymous().WithTags("Payment webhooks");
    }

    private static async Task<WebhookResponse> ReceiveAsync(
        string provider,
        WebhookRequest r,
        PaymentsDbContext db,
        PaymentProcessor processor,
        SecretProtector protector,
        IOptions<PaymentOptions> options,
        CancellationToken ct)
    {
        var code = PaymentProvider.SupportedCodes.FirstOrDefault(c => string.Equals(c, provider, StringComparison.OrdinalIgnoreCase))
            ?? throw new NotFoundException("Payment provider", provider);

        var errors = new ValidationErrors();
        CommonRules.Required(errors, "EventId", r.EventId, "Event id", 100);
        CommonRules.Required(errors, "TransactionRef", r.TransactionRef, "Transaction reference", 30);
        CommonRules.Positive(errors, "Amount", r.Amount, "Amount");
        if (r.Status is not ("success" or "failed"))
        {
            errors.Add("Status", "Status must be 'success' or 'failed'.");
        }

        errors.ThrowIfAny();

        var secret = await SecretForAsync(db, protector, options.Value, code, ct);
        if (secret is null || !WebhookSignature.Verify(secret, r.EventId!, r.TransactionRef!, r.Status!, r.Amount!.Value, r.Signature))
        {
            throw new UnauthorizedException("The webhook signature is missing or wrong.", "webhook.invalid_signature");
        }

        var seen = await db.WebhookEvents.AsNoTracking().FirstOrDefaultAsync(e => e.ProviderCode == code && e.EventId == r.EventId, ct);
        if (seen is not null)
        {
            return new WebhookResponse(seen.Outcome, Duplicate: true);
        }

        var payment = await db.Payments.FirstOrDefaultAsync(p => p.TransactionRef == r.TransactionRef && p.Method == code, ct);
        var outcome = payment is null ? "unknown_payment" : await ApplyAsync(processor, payment, r, ct);

        var record = new PaymentWebhookEvent(code, r.EventId!, r.TransactionRef!, r.Status!, r.Amount.Value, processor.UtcNow);
        record.Resolve(payment, outcome);
        db.WebhookEvents.Add(record);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Two deliveries of the same event raced; the other one wrote the record and the payment change is idempotent.
            return new WebhookResponse(outcome, Duplicate: true);
        }

        return new WebhookResponse(outcome, Duplicate: false);
    }

    private static async Task<string> ApplyAsync(PaymentProcessor processor, Payment payment, WebhookRequest r, CancellationToken ct)
    {
        if (r.Status == "failed")
        {
            return await processor.ApplyFailureAsync(payment, "Declined by the provider.", ct);
        }

        // Never mark a payment paid for a different amount: keep it as it is and let reconciliation show the difference.
        return r.Amount != payment.Amount ? "amount_mismatch" : await processor.ApplySuccessAsync(payment, r.ProviderReference, ct);
    }

    /// <summary>The key that signs this provider's webhooks: its own secret key, or the configured one for the Mock. Null when there is none.</summary>
    internal static async Task<string?> SecretForAsync(PaymentsDbContext db, SecretProtector protector, PaymentOptions options, string code, CancellationToken ct)
    {
        var provider = await db.PaymentProviders.AsNoTracking().FirstOrDefaultAsync(p => p.Code == code, ct);
        if (provider?.SecretKeyEncrypted is not null)
        {
            return protector.Unprotect(provider.SecretKeyEncrypted);
        }

        return code == PaymentProvider.MockCode ? options.MockWebhookSecret : null;
    }
}
