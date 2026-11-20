using System.Text.Json;
using AICOS.BuildingBlocks.Exceptions;
using AICOS.BuildingBlocks.Security;
using AICOS.Payments.Api.Domain;
using AICOS.Payments.Api.Features.Payments;
using AICOS.Payments.Api.Infrastructure;
using AICOS.Payments.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AICOS.Payments.Api.Features.SessionBilling;

public sealed class BillPayOsService(PaymentsDbContext db, BillLedgerService ledger, BillWebhookReceiver receiver,
    PayOsGateway gateway, IOptions<PayOsOptions> options)
{
    private async Task<BillAttempt?> FindAsync(long code, CancellationToken ct)
    {
        var prefix = PayOs.BillReferencePrefixOf(code);
        if (prefix is null) return null;
        var rows = await db.BillAttempts.AsNoTracking().Where(x => x.Provider == PaymentProvider.PayOsCode && x.TransactionRef.StartsWith(prefix))
            .OrderBy(x => x.Id).Take(2).ToListAsync(ct);
        if (rows.Count > 1) throw new ConflictException("Ambiguous bank reference requires reconciliation.", "payment.reference_conflict");
        return rows.SingleOrDefault();
    }

    // Called only after PayOsEndpoints verifies the provider's original HMAC payload.
    internal async Task<object> ReceiveVerifiedAsync(JsonElement body, CancellationToken ct)
    {
        var data = body.GetProperty("data");
        var code = data.GetProperty("orderCode").GetInt64();
        var attempt = await FindAsync(code, ct);
        if (attempt is null) return new { Outcome = "unknown_bill_payment", Duplicate = false };
        if (!PayOsStaffPolicy.MatchesChannel(options.Value, attempt.TenantId))
            throw new UnauthorizedException("The configured payOS channel does not own this bill.", "webhook.channel_mismatch");
        if (data.GetProperty("code").GetString() != "00") return new { Outcome = "ignored_not_paid", Duplicate = false };
        var amount = data.GetProperty("amount").GetInt64();
        var reference = data.GetProperty("reference").GetString();
        if (amount <= 0 || string.IsNullOrWhiteSpace(reference) || reference.Length > 128)
            throw new ValidationFailedException("receipt", "Invalid payOS receipt identity.");
        var issue = data.GetProperty("currency").GetString() != "VND" ? "CURRENCY_MISMATCH"
            : data.GetProperty("accountNumber").GetString() != options.Value.ReceivingAccountNumber ? "RECEIVER_MISMATCH" : null;
        return await receiver.ApplyVerifiedPayOsAsync(new("payos:" + code + ":" + BillIdentityHash(reference),
            attempt.TransactionRef, "success", amount, reference, null), issue, ct);
    }

    public async Task<BillView> QueryAsync(Guid billId, Guid paymentId, ICurrentUser user, CancellationToken ct)
    {
        var bill = await ledger.LoadAsync(billId, user, ct);
        if (!PayOsStaffPolicy.MatchesChannel(options.Value, bill.TenantId)) throw new DependencyUnavailableException("payos-channel");
        var attempt = await db.BillAttempts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == paymentId && x.BillId == billId
            && x.TenantId == bill.TenantId && x.Provider == PaymentProvider.PayOsCode, ct) ?? throw new NotFoundException("Bill payment", paymentId);
        var result = await gateway.QueryBillAsync(attempt.TransactionRef, attempt.Amount, ct);
        if (result.FailureReason == "payment.amount_mismatch")
            await receiver.ApplyVerifiedPayOsAsync(new("query-mismatch:" + attempt.Id + ":" + result.ReceivedAmount, attempt.TransactionRef,
                "success", result.ReceivedAmount, null, null), "AMOUNT_MISMATCH", ct);
        else if (result.State == ProviderState.Succeeded && result.ReceivedAmount == attempt.Amount && result.ProviderReference is { Length: > 0 and <= 128 })
            await receiver.ApplyVerifiedPayOsAsync(new("query-paid:" + attempt.Id + ":" + BillIdentityHash(result.ProviderReference),
                attempt.TransactionRef, "success", attempt.Amount, result.ProviderReference, null), null, ct);
        else if (result.State == ProviderState.Failed)
            await receiver.ApplyVerifiedPayOsAsync(new("query-failed:" + attempt.Id, attempt.TransactionRef,
                "failed", attempt.Amount, null, null), null, ct);
        return await ledger.GetByIdAsync(billId, user, false, ct);
    }

    internal async Task<(Guid BillId, string Status)?> ReturnAsync(long code, CancellationToken ct)
    {
        var attempt = await FindAsync(code, ct);
        if (attempt is null) return null;
        var bill = await db.SessionBills.AsNoTracking().SingleAsync(x => x.Id == attempt.BillId, ct);
        var view = await ledger.ViewAsync(bill, ct);
        return (bill.Id, view.NeedsReconciliation ? "REVIEW" : view.Status);
    }

    private static string BillIdentityHash(string value) => AICOS.Contracts.Billing.BillIdentity.Hash(value)[..48];
}
