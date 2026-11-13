using System.Text.Json;
using AICOS.BuildingBlocks.Exceptions;
using AICOS.BuildingBlocks.Security;
using AICOS.Contracts.Billing;
using AICOS.Payments.Api.Domain;
using AICOS.Payments.Api.Infrastructure;
using AICOS.Payments.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AICOS.Payments.Api.Features.SessionBilling;

public sealed class BillLedgerService(PaymentsDbContext db, IBillOrderingClient ordering, GatewayRegistry gateways,
    IConfiguration config, IHostEnvironment environment, TimeProvider time, BillOutboxDispatcher dispatcher)
{
    private DateTime Now => time.GetUtcNow().UtcDateTime;
    public async Task<BillView> GetAsync(Guid session, ICurrentUser user, CancellationToken ct)
    {
        var tenant = TenantScope.Require(user);
        var saved = await db.SessionBills.AsNoTracking().FirstOrDefaultAsync(b => b.TenantId == tenant && b.SessionId == session, ct);
        if (saved is not null) { BranchAccess.EnsureCanAccess(user, saved.BranchId); return await ViewAsync(saved, ct); }
        var quote = await ordering.QuoteAsync(session, ct);
        Scope(quote, user);
        return new(BillIdentity.For(tenant, session), session, quote.BranchId, "DRAFT", 0, quote.Fingerprint, quote, false, false, null, false, TestQrAllowed());
    }
    public async Task<BillView> GetByIdAsync(Guid id, ICurrentUser user, bool invoice, CancellationToken ct)
    {
        var bill = await LoadAsync(id, user, ct);
        var view = await ViewAsync(bill, ct);
        if (invoice && (bill.Status is not ("PAID" or "CLOSED") || view.NeedsReconciliation))
            throw new ConflictException("A verified paid bill is required.", "billing.not_paid");
        return view;
    }
    public async Task<BillView> CollectAsync(Guid id, CollectBillRequest request, string key, string method, ICurrentUser user, CancellationToken ct)
    {
        var tenant = TenantScope.Require(user);
        var actor = user.UserId ?? throw new UnauthorizedException("Sign in first.");
        if (key.Length is < 8 or > 80 || request.SessionId == Guid.Empty || request.Fingerprint?.Length != 64 || id != BillIdentity.For(tenant, request.SessionId))
            throw new ValidationFailedException("request", "Session, Bill identity, fingerprint and Idempotency-Key (8-80 characters) are required.");
        if (method == "Cash" && (!request.ConfirmReceived || request.AmountTendered is null || request.AmountTendered != decimal.Truncate(request.AmountTendered.Value)
            || request.AmountTendered > 100_000_000m))
            throw new ValidationFailedException("ConfirmReceived", "Cashier must explicitly confirm whole VND received.");
        var keyHash = BillIdentity.Hash($"{tenant}:{actor}:{key}");
        var requestHash = BillIdentity.Hash(JsonSerializer.Serialize(new { id, method, request }));
        var replay = await db.BillAttempts.AsNoTracking().FirstOrDefaultAsync(a => a.TenantId == tenant && a.KeyHash == keyHash, ct);
        if (replay is not null)
        {
            if (replay.BillId != id || replay.RequestHash != requestHash) throw new ConflictException("Key was used for a different confirmation.", "payment.request_key_conflict");
            await dispatcher.TryFastAsync(id, ct);
            return await GetByIdAsync(id, user, false, ct);
        }
        var quote = await ordering.QuoteAsync(request.SessionId, ct);
        Scope(quote, user);
        if (quote.Fingerprint != request.Fingerprint) throw new ConflictException("Bill contents changed. Reload before collecting.", "billing.stale");
        if (quote.Total <= 0 || quote.Total > 100_000_000 || quote.Total != decimal.Truncate(quote.Total))
            throw new ConflictException("Zero/invalid total requires a separate authorized workflow.", "billing.amount");
        if (method == "Cash" && request.AmountTendered < quote.Total)
            throw new ValidationFailedException("AmountTendered", "Insufficient cash received.");
        IPaymentGateway? gateway = null;
        if (method == "QR")
        {
            if (request.ProviderCode != "Mock" || !TestQrAllowed())
                throw new DependencyUnavailableException("verified-bill-qr-provider");
            gateway = gateways.For("Mock");
        }
        // Freeze commits in Ordering BEFORE local collection. No distributed transaction.
        // A crash after freeze is recovered using the same deterministic bill id and fingerprint.
        quote = await ordering.FreezeAsync(request.SessionId, new(tenant, quote.BranchId, id, request.Fingerprint), ct);
        Scope(quote, user);
        if (quote.Fingerprint != request.Fingerprint || quote.SessionId != request.SessionId)
            throw new ConflictException("Frozen snapshot differs.", "billing.stale");
        var stableAttemptId = new Guid(Convert.FromHexString(keyHash)[..16]);
        var result = await BillLock.RunAsync(db, id, async () =>
        {
            db.ChangeTracker.Clear();
            var existing = await db.BillAttempts.FirstOrDefaultAsync(a => a.TenantId == tenant && a.KeyHash == keyHash, ct);
            if (existing is not null)
            {
                if (existing.BillId != id || existing.RequestHash != requestHash) throw new ConflictException("Request key differs.", "payment.request_key_conflict");
                return (Attempt: existing, Created: false);
            }
            var bill = await db.SessionBills.FirstOrDefaultAsync(b => b.Id == id && b.TenantId == tenant, ct);
            if (bill is not null && (bill.Status != "UNPAID" || bill.NeedsReconciliation || bill.Fingerprint != quote.Fingerprint))
                throw new ConflictException("Bill is paid or requires reconciliation.", "billing.not_collectible");
            if (await db.BillAttempts.AnyAsync(a => a.BillId == id && a.Status == "PENDING", ct))
                throw new ConflictException("Resolve the existing QR first.", "payment.online_pending");
            var orderIds = quote.Orders.Select(o => o.OrderId).ToArray();
            var legacy = await db.Payments.AsNoTracking().Where(p => p.TenantId == tenant && orderIds.Contains(p.OrderId)).ToListAsync(ct);
            if (legacy.Any(p => p.IsPaid || p.RequiresReconciliation || p.RefundedAmount > 0))
                throw new ConflictException("Legacy money must be reconciled before collecting a bill.", "billing.legacy_payment");
            if (bill is null)
            {
                bill = new SessionBill { Id = id, TenantId = tenant, BranchId = quote.BranchId, SessionId = quote.SessionId,
                    Fingerprint = quote.Fingerprint, QuoteJson = JsonSerializer.Serialize(quote), Total = quote.Total, CreatedByUserId = actor, CreatedAtUtc = Now };
                db.SessionBills.Add(bill);
                db.SessionBillOrders.AddRange(quote.Orders.Select(o => new SessionBillOrder { BillId = id, OrderId = o.OrderId, Amount = o.Amount }));
            }
            var attempt = new BillAttempt { Id = stableAttemptId, TenantId = tenant, BillId = id, CreatedByUserId = actor, KeyHash = keyHash,
                RequestHash = requestHash, Method = method, Provider = method == "QR" ? "Mock" : null, Amount = quote.Total,
                Tendered = method == "Cash" ? request.AmountTendered : null, TransactionRef = "BILL" + stableAttemptId.ToString("N"), CreatedAtUtc = Now };
            db.BillAttempts.Add(attempt);
            AddJournal(bill, "ATTEMPT", new { attempt.Id, method, actor }, Now);
            if (method == "Cash") MarkPaid(bill, attempt, null, Now);
            await db.SaveChangesAsync(ct);
            return (Attempt: attempt, Created: true);
        }, ct, keyHash);
        if (method == "QR" && result.Created)
        {
            // Intent/reference survives timeout or a process crash. Replays NEVER call provider Create again.
            GatewayCheckout checkout;
            try { checkout = await gateway!.CreatePaymentAsync(new(result.Attempt.TransactionRef, quote.Total, "AICOS session bill", null, null, null, null, null), ct); }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            { throw new DependencyUnavailableException("bill-qr-provider"); }
            await BillLock.RunAsync(db, id, async () =>
            {
                db.ChangeTracker.Clear();
                var saved = await db.BillAttempts.SingleAsync(a => a.Id == stableAttemptId, ct);
                saved.CheckoutUrl = checkout.CheckoutUrl;
                await db.SaveChangesAsync(ct); return true;
            }, ct);
        }
        await dispatcher.TryFastAsync(id, ct);
        return await GetByIdAsync(id, user, false, ct);
    }
    internal bool TestQrAllowed() => !environment.IsProduction() && config.GetValue<bool>("SessionBilling:AllowTestProviders")
        && config["SessionBilling:TestWebhookSecret"] is { Length: >= 32 }
        && (db.Database.ProviderName == "Microsoft.EntityFrameworkCore.InMemory"
            || (db.Database.IsRelational() && db.Database.GetDbConnection().Database == "aicos_billflow_test"));
    internal async Task<SessionBill> LoadAsync(Guid id, ICurrentUser user, CancellationToken ct)
    {
        var tenant = TenantScope.Require(user);
        var b = await db.SessionBills.AsNoTracking().FirstOrDefaultAsync(b => b.Id == id && b.TenantId == tenant, ct) ?? throw new NotFoundException("Bill", id);
        BranchAccess.EnsureCanAccess(user, b.BranchId); return b;
    }
    private static void Scope(SessionBillQuote quote, ICurrentUser user)
    {
        if (quote.TenantId != TenantScope.Require(user)) throw new NotFoundException("Session", quote.SessionId);
        BranchAccess.EnsureCanAccess(user, quote.BranchId);
    }
    internal async Task<BillView> ViewAsync(SessionBill bill, CancellationToken ct)
    {
        var attempt = bill.PaidPaymentId is { } paid ? await db.BillAttempts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == paid && a.BillId == bill.Id && a.TenantId == bill.TenantId, ct)
            : await db.BillAttempts.AsNoTracking().Where(a => a.BillId == bill.Id).OrderByDescending(a => a.Status == "PENDING").ThenByDescending(a => a.CreatedAtUtc).ThenByDescending(a => a.Id).FirstOrDefaultAsync(ct);
        var paidState = bill.Status is "PAID" or "CLOSED";
        var invalidProof = paidState && (attempt?.Status != "SUCCEEDED" || attempt.Amount != bill.Total || attempt.PaidAtUtc is null);
        var pendingRisk = paidState && await db.BillAttempts.AnyAsync(a => a.BillId == bill.Id && a.Status == "PENDING", ct);
        return BillView.From(bill, attempt, attempt?.Provider == "Mock") with { QrAvailable = TestQrAllowed(),
            NeedsReconciliation = bill.NeedsReconciliation || invalidProof || pendingRisk || await LegacyRiskAsync(db, bill, ct) };
    }
    internal static async Task<bool> LegacyRiskAsync(PaymentsDbContext db, SessionBill bill, CancellationToken ct)
    {
        var ids = JsonSerializer.Deserialize<SessionBillQuote>(bill.QuoteJson)!.Orders.Select(o => o.OrderId).ToArray();
        var legacy = await db.Payments.AsNoTracking().Where(p => p.TenantId == bill.TenantId && ids.Contains(p.OrderId)).ToListAsync(ct);
        return legacy.Any(p => p.IsPaid || p.RequiresReconciliation || p.RefundedAmount > 0);
    }
    internal void AddJournal(SessionBill bill, string kind, object payload, DateTime now, Guid? id = null) =>
        db.BillJournal.Add(new BillJournal { Id = id ?? Guid.NewGuid(), BillId = bill.Id, Kind = kind, Payload = JsonSerializer.Serialize(payload),
            CreatedAtUtc = now, NextAttemptAtUtc = now, DeliveredAtUtc = kind == "PAID" ? null : now });
    internal void MarkPaid(SessionBill bill, BillAttempt attempt, string? reference, DateTime now)
    {
        if (attempt.Status == "SUCCEEDED") return;
        attempt.Status = "SUCCEEDED"; attempt.PaidAtUtc = now; attempt.ProviderReference = reference;
        if (bill.PaidPaymentId is { } paid && paid != attempt.Id)
        {
            bill.NeedsReconciliation = true; bill.Version++;
            AddJournal(bill, "DUPLICATE_RECEIPT", new { attempt.Id, reference }, now); return;
        }
        bill.Status = "PAID"; bill.PaidPaymentId = attempt.Id; bill.PaidAtUtc = now; bill.Version++;
        AddJournal(bill, "PAID", new BillPaidNotice(bill.TenantId, bill.BranchId, bill.SessionId, bill.Id, bill.Fingerprint, attempt.Id, attempt.Method, now), now, attempt.Id);
    }
}
