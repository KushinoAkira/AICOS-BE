using System.Text.Json;
using AICOS.BuildingBlocks.Exceptions;
using AICOS.Contracts.Billing;
using AICOS.Payments.Api.Features.Payments;
using AICOS.Payments.Api.Infrastructure;
using AICOS.Payments.Api.Persistence;
using Microsoft.EntityFrameworkCore;
namespace AICOS.Payments.Api.Features.SessionBilling;

public sealed class BillWebhookReceiver(PaymentsDbContext db, BillLedgerService ledger, IConfiguration config, TimeProvider time, BillOutboxDispatcher dispatcher)
{
    private sealed record ReceiptIdentity(string Provider, string TransactionRef, string Status, decimal Amount);
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    public async Task<object> ReceiveAsync(string provider, WebhookRequest request, CancellationToken ct)
    {
        // Existing gateways are simulated. Do not present generic HMAC as production bank verification.
        if (provider != "Mock" || !ledger.TestQrAllowed()) throw new DependencyUnavailableException("verified-bill-qr-provider");
        if (string.IsNullOrWhiteSpace(request.EventId) || request.EventId.Length > 100 || request.TransactionRef?.Length is not > 0 or > 64
            || request.Status is not ("success" or "failed") || request.Amount is null || request.Amount <= 0 || request.Amount > 100_000_000m || request.Amount != decimal.Truncate(request.Amount.Value))
            throw new ValidationFailedException("webhook", "Invalid signed receipt fields.");
        if (!WebhookSignature.Verify(config["SessionBilling:TestWebhookSecret"]!, request.EventId, request.TransactionRef!, request.Status!, request.Amount.Value, request.Signature))
            throw new UnauthorizedException("Invalid receipt signature.", "webhook.invalid_signature");
        var attempt = await db.BillAttempts.AsNoTracking().FirstOrDefaultAsync(a => a.TransactionRef == request.TransactionRef && a.Provider == provider, ct)
            ?? throw new NotFoundException("Bill payment", "reference");
        var mismatch = attempt.Amount != request.Amount;
        var eventHash = BillIdentity.Hash($"{attempt.TenantId}:{provider}:{request.EventId}");
        var receiptId = new Guid(Convert.FromHexString(eventHash)[..16]);
        var identity = new ReceiptIdentity(provider, request.TransactionRef!, request.Status!, request.Amount.Value);
        var payload = JsonSerializer.Serialize(identity, Web);
        var duplicate = await BillLock.RunAsync(db, attempt.BillId, async () =>
        {
            db.ChangeTracker.Clear();
            var seen = await db.BillJournal.FirstOrDefaultAsync(e => e.Id == receiptId, ct);
            if (seen is not null)
            {
                if (seen.BillId != attempt.BillId || JsonSerializer.Deserialize<ReceiptIdentity>(seen.Payload, Web) != identity) throw new ConflictException("Receipt replay differs.", "billing.receipt_conflict");
                return true;
            }
            var row = await db.BillAttempts.SingleAsync(a => a.Id == attempt.Id, ct);
            var bill = await db.SessionBills.SingleAsync(b => b.Id == row.BillId, ct);
            var now = time.GetUtcNow().UtcDateTime;
            if (mismatch)
            {
                bill.NeedsReconciliation = true; bill.Version++;
                ledger.AddJournal(bill, "AMOUNT_MISMATCH", new { row.Id, request.Amount, Expected = row.Amount }, now);
            }
            else if (request.Status == "success") ledger.MarkPaid(bill, row, request.EventId, now);
            else if (row.Status == "PENDING") row.Status = "FAILED";
            db.BillJournal.Add(new BillJournal { Id = receiptId, BillId = bill.Id, Kind = "PROVIDER_RECEIPT", Payload = payload,
                CreatedAtUtc = now, DeliveredAtUtc = now, NextAttemptAtUtc = now });
            await db.SaveChangesAsync(ct); return false;
        }, ct, eventHash);
        await dispatcher.TryFastAsync(attempt.BillId, ct);
        return new { Accepted = true, Duplicate = duplicate, ReconciliationRequired = mismatch };
    }
}
