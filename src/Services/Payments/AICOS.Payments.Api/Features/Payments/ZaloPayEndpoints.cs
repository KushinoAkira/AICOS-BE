using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using AICOS.Payments.Api.Domain;
using AICOS.Payments.Api.Infrastructure;
using AICOS.Payments.Api.Persistence;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AICOS.Payments.Api.Features.Payments;

/// <summary>ZaloPay's answer to a callback: 1 handled, 2 seen already, -1 refused (bad mac or not configured).</summary>
public sealed record ZaloPayCallbackResponse([property: JsonPropertyName("return_code")] int ReturnCode, [property: JsonPropertyName("return_message")] string ReturnMessage);

/// <summary>
/// What ZaloPay calls back (anonymous: ZaloPay holds no AICOS token).
/// <list type="bullet">
/// <item>POST /zalopay/callback: ZaloPay reports a paid order. The mac (HMAC-SHA256 of the data string with key2) is the authentication.
/// Idempotent. Answers ZaloPay's own format: return_code 1 when handled, -1 when the mac is wrong.</item>
/// <item>GET /zalopay/return: the customer's browser coming back from ZaloPay's page. Its query is not trusted: the status is asked from
/// ZaloPay, applied, and the browser is redirected to the result page of the web app. On a local machine, where ZaloPay cannot reach the
/// callback, this is what records the payment.</item>
/// </list>
/// </summary>
public static class ZaloPayEndpoints
{
    public static void MapZaloPayEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/zalopay").WithTags("ZaloPay").AllowAnonymous();
        group.MapPost("/callback", CallbackAsync);
        group.MapGet("/return", ReturnAsync);
    }

    private static async Task<ZaloPayCallbackResponse> CallbackAsync(
        JsonElement body, PaymentsDbContext db, PaymentProcessor processor, SecretProtector protector, IOptions<ZaloPayOptions> options, CancellationToken ct)
    {
        var credentials = await ZaloPayCredentials.LoadAsync(db, protector, ct);
        if (credentials is null || !ZaloPay.VerifyCallback(credentials.Key2, body, out var data) || ZaloPay.Text(data, "app_id") != credentials.AppId)
        {
            return new ZaloPayCallbackResponse(-1, "mac not equal");
        }

        var appTransId = ZaloPay.Text(data, "app_trans_id");
        var transactionRef = ZaloPay.TransactionRefOf(appTransId);
        var zpTransId = ZaloPay.Text(data, "zp_trans_id") ?? string.Empty;
        var amount = long.TryParse(ZaloPay.Text(data, "amount"), NumberStyles.None, CultureInfo.InvariantCulture, out var a) ? a : -1;
        var payment = transactionRef is null
            ? null
            : await db.Payments.FirstOrDefaultAsync(p => p.Method == ZaloPay.Code && p.TransactionRef == transactionRef, ct);
        if (payment is null)
        {
            return new ZaloPayCallbackResponse(1, "unknown order");
        }

        var eventId = $"{appTransId}:{zpTransId}";
        eventId = eventId.Length > 100 ? eventId[..100] : eventId;
        if (await db.WebhookEvents.AsNoTracking().AnyAsync(e => e.ProviderCode == ZaloPay.Code && e.EventId == eventId, ct))
        {
            return new ZaloPayCallbackResponse(2, "already handled");
        }

        // ZaloPay calls back only for paid orders. Never mark a payment paid for a different amount.
        // With the failure simulation on (testing only), the paid order is taken as refused.
        var simulated = ZaloPay.SimulatedFailure(options.Value.SimulateFailure);
        var outcome = amount != payment.Amount ? "amount_mismatch"
            : simulated is not null ? await processor.ApplyFailureAsync(payment, simulated, ct)
            : await processor.ApplySuccessAsync(payment, zpTransId, ct);

        var record = new PaymentWebhookEvent(ZaloPay.Code, eventId, payment.TransactionRef, "success", amount, processor.UtcNow);
        record.Resolve(payment, outcome);
        db.WebhookEvents.Add(record);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            return new ZaloPayCallbackResponse(2, "already handled");
        }

        return new ZaloPayCallbackResponse(1, "success");
    }

    private static async Task<IResult> ReturnAsync(
        string? apptransid, PaymentsDbContext db, PaymentProcessor processor, GatewayRegistry gateways, IOptions<ZaloPayOptions> options, CancellationToken ct)
    {
        var transactionRef = ZaloPay.TransactionRefOf(apptransid);
        var payment = transactionRef is null
            ? null
            : await db.Payments.FirstOrDefaultAsync(p => p.Method == ZaloPay.Code && p.TransactionRef == transactionRef, ct);
        var query = new Dictionary<string, string?>();
        if (payment is null)
        {
            query["status"] = "invalid";
            return Results.Redirect(QueryHelpers.AddQueryString(options.Value.ResultPageUrl, query));
        }

        if (!payment.IsPaid)
        {
            var status = await gateways.For(ZaloPay.Code).QueryAsync(payment, ct);
            if (status.State == ProviderState.Succeeded)
            {
                await processor.ApplySuccessAsync(payment, status.ProviderReference, ct);
            }
            else if (status.State == ProviderState.Failed && payment.Status == PaymentStatus.Pending)
            {
                await processor.ApplyFailureAsync(payment, status.FailureReason ?? "ZaloPay reports the payment as failed.", ct);
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
