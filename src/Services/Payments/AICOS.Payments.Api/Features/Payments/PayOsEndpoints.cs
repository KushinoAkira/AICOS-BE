using System.Text.Json;
using AICOS.BuildingBlocks.Exceptions;
using AICOS.Payments.Api.Domain;
using AICOS.Payments.Api.Infrastructure;
using AICOS.Payments.Api.Persistence;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AICOS.Payments.Api.Features.Payments;

public sealed record PayOsWebhookResponse(string Outcome, bool Duplicate);

/// <summary>
/// What payOS calls back (anonymous: payOS holds no AICOS token).
/// <list type="bullet">
/// <item>POST /payos/webhook: payOS reports a received transfer. The HMAC-SHA256 signature with the checksum key is the authentication.
/// Idempotent, and always 200 once the signature is right (also for the test call payOS makes when the webhook URL is saved).</item>
/// <item>GET /payos/return: the customer's browser coming back after paying or cancelling. Its query is not signed, so it is only a hint:
/// the status is read from payOS itself, applied, and the browser is redirected to the result page of the web app. On a local machine,
/// where payOS cannot reach the webhook, this is what records the payment.</item>
/// </list>
/// </summary>
public static class PayOsEndpoints
{
    public static void MapPayOsEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/payos").WithTags("payOS").AllowAnonymous();
        group.MapPost("/webhook", WebhookAsync);
        group.MapGet("/return", ReturnAsync);
    }

    private static async Task<PayOsWebhookResponse> WebhookAsync(
        JsonElement body, PaymentsDbContext db, PaymentProcessor processor, SecretProtector protector, CancellationToken ct)
    {
        var credentials = await PayOsCredentials.LoadAsync(db, protector, ct) ?? throw new NotFoundException("Payment provider", PaymentProvider.PayOsCode);
        if (!PayOsSignature.VerifyWebhook(credentials.ChecksumKey, body))
        {
            throw new UnauthorizedException("The webhook signature is missing or wrong.", "webhook.invalid_signature");
        }

        // VerifyWebhook checked that these fields exist with these types.
        var data = body.GetProperty("data");
        var orderCode = data.GetProperty("orderCode").GetInt64();
        var amount = data.GetProperty("amount").GetInt64();
        var reference = data.GetProperty("reference").GetString()!;
        var paid = data.GetProperty("code").GetString() == "00"
            && !(body.TryGetProperty("code", out var top) && top.ValueKind == JsonValueKind.String && top.GetString() != "00");

        var payment = await FindAsync(db, orderCode, ct);
        if (payment is null)
        {
            return new PayOsWebhookResponse("unknown_payment", Duplicate: false);
        }

        var eventId = $"{orderCode}:{reference}";
        eventId = eventId.Length > 100 ? eventId[..100] : eventId;
        var seen = await db.WebhookEvents.AsNoTracking().FirstOrDefaultAsync(e => e.ProviderCode == PaymentProvider.PayOsCode && e.EventId == eventId, ct);
        if (seen is not null)
        {
            return new PayOsWebhookResponse(seen.Outcome, Duplicate: true);
        }

        // Never mark a payment paid for a different amount (payOS lets a customer transfer less): reconciliation shows the difference.
        var outcome = !paid ? "ignored_not_paid"
            : amount != payment.Amount ? "amount_mismatch"
            : await processor.ApplySuccessAsync(payment, reference, ct);

        var record = new PaymentWebhookEvent(PaymentProvider.PayOsCode, eventId, payment.TransactionRef, paid ? "success" : "failed", amount, processor.UtcNow);
        record.Resolve(payment, outcome);
        db.WebhookEvents.Add(record);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Two deliveries of the same event raced; the other one wrote the record and the payment change is idempotent.
            return new PayOsWebhookResponse(outcome, Duplicate: true);
        }

        return new PayOsWebhookResponse(outcome, Duplicate: false);
    }

    private static async Task<IResult> ReturnAsync(
        long? orderCode, PaymentsDbContext db, PaymentProcessor processor, GatewayRegistry gateways, IOptions<PayOsOptions> options, CancellationToken ct)
    {
        var payment = orderCode is { } code ? await FindAsync(db, code, ct) : null;
        var query = new Dictionary<string, string?>();
        if (payment is null)
        {
            query["status"] = "invalid";
            return Results.Redirect(QueryHelpers.AddQueryString(options.Value.ResultPageUrl, query));
        }

        if (!payment.IsPaid)
        {
            var status = await gateways.For(PaymentProvider.PayOsCode).QueryAsync(payment, ct);
            if (status.State == ProviderState.Succeeded)
            {
                await processor.ApplySuccessAsync(payment, status.ProviderReference, ct);
            }
            else if (status.State == ProviderState.Failed && payment.Status == PaymentStatus.Pending)
            {
                await processor.ApplyFailureAsync(payment, status.FailureReason ?? "payOS reports the payment as failed.", ct);
            }
        }

        query["status"] = payment.IsPaid ? "paid" : payment.Status == PaymentStatus.Pending ? "pending" : "failed";
        query["paymentId"] = payment.Id.ToString();
        query["orderId"] = payment.OrderId.ToString();
        if (query["status"] == "failed")
        {
            query["reason"] = payment.FailureReason;
        }

        return Results.Redirect(QueryHelpers.AddQueryString(options.Value.ResultPageUrl, query));
    }

    private static async Task<Payment?> FindAsync(PaymentsDbContext db, long orderCode, CancellationToken ct)
    {
        var prefix = PayOs.TransactionRefPrefixOf(orderCode);
        return prefix is null
            ? null
            : await db.Payments.FirstOrDefaultAsync(p => p.Method == PaymentProvider.PayOsCode && p.TransactionRef.StartsWith(prefix), ct);
    }
}
