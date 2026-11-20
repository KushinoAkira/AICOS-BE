using System.Globalization;
using System.Text.Json;
using AICOS.BuildingBlocks.Exceptions;
using AICOS.Payments.Api.Domain;
using AICOS.Payments.Api.Infrastructure;
using AICOS.Payments.Api.Persistence;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AICOS.Payments.Api.Features.Payments;

/// <summary>
/// What MoMo calls back (anonymous: MoMo holds no AICOS token).
/// <list type="bullet">
/// <item>POST /momo/ipn: MoMo reports the result of a payment. The HMAC-SHA256 signature with the secret key is the authentication.
/// Idempotent; answers 204 as MoMo expects once the signature is right.</item>
/// <item>GET /momo/return: the customer's browser coming back from MoMo's page. Its query is not trusted: the status is asked from MoMo,
/// applied, and the browser is redirected to the result page of the web app. On a local machine, where MoMo cannot reach the IPN, this is
/// what records the payment.</item>
/// </list>
/// </summary>
public static class MomoEndpoints
{
    public static void MapMomoEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/momo").WithTags("MoMo").AllowAnonymous();
        group.MapPost("/ipn", IpnAsync);
        group.MapGet("/return", ReturnAsync);
    }

    private static async Task<IResult> IpnAsync(JsonElement body, PaymentsDbContext db, PaymentProcessor processor, SecretProtector protector, CancellationToken ct)
    {
        var credentials = await MomoCredentials.LoadAsync(db, protector, ct) ?? throw new NotFoundException("Payment provider", PaymentProvider.MomoCode);
        if (!MomoSignature.VerifyIpn(credentials, body))
        {
            throw new UnauthorizedException("The IPN signature is missing or wrong.", "webhook.invalid_signature");
        }

        // VerifyIpn checked that every signed field is there.
        var orderId = MomoSignature.Text(body, "orderId")!;
        var transId = MomoSignature.Text(body, "transId")!;
        var resultCode = int.TryParse(MomoSignature.Text(body, "resultCode"), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var code) ? code : -1;
        var amount = long.TryParse(MomoSignature.Text(body, "amount"), NumberStyles.None, CultureInfo.InvariantCulture, out var a) ? a : -1;

        var payment = await db.Payments.FirstOrDefaultAsync(p => p.Method == PaymentProvider.MomoCode && p.TransactionRef == orderId, ct);
        if (payment is null)
        {
            return Results.NoContent();
        }

        var eventId = $"{orderId}:{transId}:{resultCode}";
        eventId = eventId.Length > 100 ? eventId[..100] : eventId;
        if (await db.WebhookEvents.AsNoTracking().AnyAsync(e => e.ProviderCode == PaymentProvider.MomoCode && e.EventId == eventId, ct))
        {
            return Results.NoContent();
        }

        var state = MomoSignature.StateOf(resultCode);
        var outcome = state switch
        {
            ProviderState.Succeeded when amount != payment.Amount => "amount_mismatch",
            ProviderState.Succeeded => await processor.ApplySuccessAsync(payment, transId, ct),
            ProviderState.Failed when payment.Status == PaymentStatus.Pending =>
                await processor.ApplyFailureAsync(payment, $"MoMo: {MomoSignature.Text(body, "message")} ({resultCode}).", ct),
            _ => "ignored_not_final",
        };

        var record = new PaymentWebhookEvent(PaymentProvider.MomoCode, eventId, payment.TransactionRef, state == ProviderState.Succeeded ? "success" : state == ProviderState.Failed ? "failed" : "pending", amount, processor.UtcNow);
        record.Resolve(payment, outcome);
        db.WebhookEvents.Add(record);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Two deliveries of the same IPN raced; the other one wrote the record and the payment change is idempotent.
        }

        return Results.NoContent();
    }

    private static async Task<IResult> ReturnAsync(
        string? orderId, PaymentsDbContext db, PaymentProcessor processor, GatewayRegistry gateways, IOptions<MomoOptions> options, CancellationToken ct)
    {
        var payment = string.IsNullOrWhiteSpace(orderId)
            ? null
            : await db.Payments.FirstOrDefaultAsync(p => p.Method == PaymentProvider.MomoCode && p.TransactionRef == orderId, ct);
        var query = new Dictionary<string, string?>();
        if (payment is null)
        {
            query["status"] = "invalid";
            return Results.Redirect(QueryHelpers.AddQueryString(options.Value.ResultPageUrl, query));
        }

        if (!payment.IsPaid)
        {
            var status = await gateways.For(PaymentProvider.MomoCode).QueryAsync(payment, ct);
            if (status.State == ProviderState.Succeeded)
            {
                await processor.ApplySuccessAsync(payment, status.ProviderReference, ct);
            }
            else if (status.State == ProviderState.Failed && payment.Status == PaymentStatus.Pending)
            {
                await processor.ApplyFailureAsync(payment, status.FailureReason ?? "MoMo reports the payment as failed.", ct);
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
}
