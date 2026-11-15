using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using AICOS.BuildingBlocks.Exceptions;
using AICOS.BuildingBlocks.Security;
using AICOS.Payments.Api.Domain;
using AICOS.Payments.Api.Infrastructure;
using AICOS.Payments.Api.Persistence;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AICOS.Payments.Api.Features.Payments;

/// <summary>ZaloPay's answer to a callback: 1 handled, 2 seen already, -1 refused (bad mac or not configured).</summary>
/// <summary>The payment shown on the test payment page.</summary>
public sealed record ZaloPayTestPayment(string TransactionRef, Guid PaymentId, Guid OrderId, decimal Amount, string Status, string Description, DateTime? ExpiresAtUtc, string? ResultUrl);

/// <summary>What the customer did on the test payment page: paid with a card (<c>Kind</c> atm or credit), or cancelled.</summary>
public sealed record ZaloPayTestPayRequest(string? Kind, string? CardNumber, string? Holder, string? Date, string? Cvv, bool? Cancel);

/// <summary>The result of the test page: where to send the browser (the result page of the web app).</summary>
public sealed record ZaloPayTestPayResponse(string Status, string? Reason, string ResultUrl);

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

        // The test payment page (ZALOPAY_TEST_PAGE, testing and demos only): the signed-in customer pays with one of ZaloPay's test cards.
        var test = api.MapGroup("/zalopay/test-page").WithTags("ZaloPay").RequireAuthorization();
        test.MapGet("/{transactionRef}", TestPageAsync);
        test.MapPost("/{transactionRef}/pay", TestPayAsync);
    }

    private static async Task<Payment> LoadTestPaymentAsync(
        string transactionRef, PaymentsDbContext db, ICurrentUser user, IOptions<ZaloPayOptions> options, CancellationToken ct)
    {
        if (!options.Value.TestPage)
        {
            throw new ForbiddenException("The ZaloPay test page is off (ZALOPAY_TEST_PAGE).", "payment.zalopay_test_page_off");
        }

        var tenantId = TenantScope.Require(user);
        var payment = await db.Payments.FirstOrDefaultAsync(p => p.TenantId == tenantId && p.Method == ZaloPay.Code && p.TransactionRef == transactionRef, ct)
            ?? throw new NotFoundException("Payment", transactionRef);
        PaymentAccess.Ensure(user, payment);
        return payment;
    }

    private static async Task<ZaloPayTestPayment> TestPageAsync(
        string transactionRef, PaymentsDbContext db, PaymentProcessor processor, ICurrentUser user, IOptions<ZaloPayOptions> options, CancellationToken ct)
    {
        var payment = await LoadTestPaymentAsync(transactionRef, db, user, options, ct);
        if (payment.Status == PaymentStatus.Pending)
        {
            await processor.ExpireIfDueAsync(payment, ct);
        }

        var result = payment.Status == PaymentStatus.Pending ? null : ResultUrl(options.Value, payment);
        return new ZaloPayTestPayment(payment.TransactionRef, payment.Id, payment.OrderId, payment.Amount, payment.Status.ToString(), payment.Description, payment.ExpiresAtUtc, result);
    }

    private static async Task<ZaloPayTestPayResponse> TestPayAsync(
        string transactionRef, ZaloPayTestPayRequest r, PaymentsDbContext db, PaymentProcessor processor, ICurrentUser user, IOptions<ZaloPayOptions> options,
        CancellationToken ct)
    {
        var payment = await LoadTestPaymentAsync(transactionRef, db, user, options, ct);
        if (payment.Status == PaymentStatus.Pending)
        {
            await processor.ExpireIfDueAsync(payment, ct);
        }

        if (payment.Status != PaymentStatus.Pending)
        {
            throw new ConflictException("This payment is no longer waiting to be paid.", "payment.not_pending");
        }

        if (r.Cancel == true)
        {
            await processor.ApplyFailureAsync(payment, ZaloPayTestCards.CancelledReason, ct);
        }
        else
        {
            var (outcome, error) = ZaloPayTestCards.Check(r.Kind, r.CardNumber, r.Holder, r.Date, r.Cvv);
            if (error is not null)
            {
                throw new BusinessRuleException(error, "The card details do not match one of ZaloPay's test cards.");
            }

            if (outcome == ZaloPayTestCards.Outcome.Paid)
            {
                await processor.ApplySuccessAsync(payment, ZaloPayTestCards.ReferenceOf(payment.TransactionRef), ct);
            }
            else
            {
                await processor.ApplyFailureAsync(payment, ZaloPayTestCards.ReasonOf(outcome!.Value)!, ct);
            }
        }

        var url = ResultUrl(options.Value, payment);
        return new ZaloPayTestPayResponse(payment.IsPaid ? "paid" : "failed", payment.IsPaid ? null : payment.FailureReason, url);
    }

    /// <summary>The result page of the web app for a payment, as the browser return redirects to it.</summary>
    private static string ResultUrl(ZaloPayOptions options, Payment payment)
    {
        var query = new Dictionary<string, string?>
        {
            ["status"] = payment.IsPaid ? "paid" : payment.Status == PaymentStatus.Pending ? "pending" : "failed",
            ["paymentId"] = payment.Id.ToString(),
            ["orderId"] = payment.OrderId.ToString(),
        };
        if (query["status"] == "failed")
        {
            query["reason"] = payment.FailureReason;
        }

        return QueryHelpers.AddQueryString(options.ResultPageUrl, query);
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

        return Results.Redirect(ResultUrl(options.Value, payment));
    }
}
