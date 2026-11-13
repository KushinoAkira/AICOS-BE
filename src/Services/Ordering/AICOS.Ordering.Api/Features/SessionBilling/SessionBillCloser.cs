using AICOS.BuildingBlocks.Exceptions;
using AICOS.BuildingBlocks.Security;
using AICOS.Contracts.Billing;
using AICOS.Contracts.Serving;
using AICOS.Ordering.Api.Domain;
using AICOS.Ordering.Api.Features.Orders;
using AICOS.Ordering.Api.Features.Qr;
using AICOS.Ordering.Api.Persistence;
using Microsoft.EntityFrameworkCore;
namespace AICOS.Ordering.Api.Features.SessionBilling;

public sealed class SessionBillCloser(OrderingDbContext db, IBillSettlementClient payments, TimeProvider clock, OrderEventPublisher events)
{
    public async Task<SessionCloseEligibility> EligibilityAsync(TableSession session, CancellationToken ct)
    {
        var unserved = await UnservedAsync(session.Id, ct);
        var fence = await db.SessionCheckouts.AsNoTracking().FirstOrDefaultAsync(x => x.SessionId == session.Id, ct);
        var proof = fence?.PaymentId is null ? null : await payments.ProofAsync(fence.BillId, ct);
        var settled = Matches(session, fence, proof);
        var reasons = new List<string>();
        if (!session.IsOpen) reasons.Add("qr.session_closed");
        if (!settled) reasons.Add("billing.not_settled");
        if (unserved > 0) reasons.Add("qr.session_unserved");
        return new(session.Id, session.BranchId, reasons.Count == 0, settled ? 0 : 1, unserved, !settled, reasons);
    }
    public async Task CloseAsync(TableSession session, CancellationToken ct)
    {
        if (!session.IsOpen) return;
        var fence = await db.SessionCheckouts.AsNoTracking().FirstOrDefaultAsync(x => x.SessionId == session.Id, ct);
        var proof = fence is null ? null : await payments.ProofAsync(fence.BillId, ct);
        if (!Matches(session, fence, proof)) throw new ConflictException("Session Bill is not settled and synchronized.", "billing.not_settled");
        var table = session.TableId;
        var closed = await TableSessionLock.RunAsync(db, session.TenantId, table, async () =>
        {
            if (db.Database.IsRelational()) await db.Entry(session).ReloadAsync(ct);
            if (!session.IsOpen) return false;
            if (session.TableId != table) throw new ConflictException("Session moved.", "qr.session_changed");
            var current = await db.SessionCheckouts.SingleAsync(x => x.SessionId == session.Id, ct);
            if (!Matches(session, current, proof) || await UnservedAsync(session.Id, ct) > 0)
                throw new ConflictException("Finish all valid servings before closing.", "qr.session_unserved");
            // Saga: Payments seals under its own lock, then Ordering commits closure. A lost ACK
            // is retried with the same proof; no transaction/SQL spans two service databases.
            await payments.SealAsync(proof!, ct);
            current.State = "Closed";
            session.Close(TableSessionCloseReason.Staff, clock.GetUtcNow().UtcDateTime);
            await db.SaveChangesAsync(ct); return true;
        }, ct);
        if (closed) await events.SessionClosedAsync(session, ct);
    }
    private static bool Matches(TableSession session, SessionCheckout? fence, BillCloseProof? proof) =>
        fence is not null && proof is not null && proof.TenantId == session.TenantId && proof.BranchId == session.BranchId
        && proof.SessionId == session.Id && proof.BillId == fence.BillId && proof.PaymentId == fence.PaymentId
        && proof.Fingerprint == fence.Fingerprint && fence.State is "Paid" or "Closed";
    private async Task<int> UnservedAsync(Guid sessionId, CancellationToken ct)
    {
        var orders = await db.Orders.AsNoTracking().Include(o => o.Items).Where(o => o.TableSessionId == sessionId).ToListAsync(ct);
        var count = 0;
        foreach (var o in orders.Where(o => !o.IsVoided))
        {
            if (o.FulfillmentMode == FulfillmentModes.WholeOrder) { if (o.DeliveredAtUtc is null) count++; continue; }
            if (o.FulfillmentMode != FulfillmentModes.CupV1) { count++; continue; }
            var p = await db.FulfillmentProjection.AsNoTracking().FirstOrDefaultAsync(p => p.OrderId == o.Id && p.TenantId == o.TenantId, ct);
            if (p is null || p.BranchId != o.BranchId || p.Total != o.Items.Sum(i => i.Quantity) || p.Served != p.Total || p.Review != 0 || p.FulfilledAtUtc is null) count++;
        }
        return count;
    }
}
