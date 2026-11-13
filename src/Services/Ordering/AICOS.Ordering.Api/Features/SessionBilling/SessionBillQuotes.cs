using AICOS.BuildingBlocks.Exceptions;
using AICOS.Contracts.Billing;
using AICOS.Ordering.Api.Domain;
using AICOS.Ordering.Api.Persistence;
using Microsoft.EntityFrameworkCore;
namespace AICOS.Ordering.Api.Features.SessionBilling;

internal static class SessionBillQuotes
{
    public static async Task<SessionBillQuote> ReadAsync(OrderingDbContext db, TableSession session, CancellationToken ct)
    {
        var held = await db.SessionCheckouts.AsNoTracking().FirstOrDefaultAsync(x => x.SessionId == session.Id, ct);
        if (held is not null) return System.Text.Json.JsonSerializer.Deserialize<SessionBillQuote>(held.QuoteJson)!;
        if (!session.IsOpen) throw new ConflictException("The session is closed.", "qr.session_closed");
        if (await db.Orders.AsNoTracking().AnyAsync(o => o.TenantId == session.TenantId && o.TableId == session.TableId
            && o.TableSessionId == null && o.Status != AICOS.Ordering.Api.Domain.Enums.OrderStatus.Completed
            && o.Status != AICOS.Ordering.Api.Domain.Enums.OrderStatus.Cancelled && o.Status != AICOS.Ordering.Api.Domain.Enums.OrderStatus.NoShow
            && o.Status != AICOS.Ordering.Api.Domain.Enums.OrderStatus.Unfulfillable, ct))
            throw new ConflictException("Unscoped legacy dine-in orders need reconciliation.", "billing.legacy_payment");
        var all = await db.Orders.AsNoTracking().Include(o => o.Items).ThenInclude(i => i.Options)
            .Where(o => o.TableSessionId == session.Id).ToListAsync(ct);
        if (all.Any(o => o.IsPaid || o.RefundedAmount > 0))
            throw new ConflictException("Legacy payments require reconciliation before opening a session bill.", "billing.legacy_payment");
        var live = all.Where(o => !o.IsVoided).OrderBy(o => o.Id).ToList();
        if (live.Count == 0) throw new ConflictException("There are no billable orders.", "billing.empty");
        var rounds = live.Select(o => new BillRound(o.Id, o.OrderCode, o.FinalAmount)).ToArray();
        var lines = live.SelectMany(o => o.Items.OrderBy(i => i.Id).Select(i => new BillLine(o.Id, o.OrderCode,
            i.ProductName, i.Quantity, i.UnitPrice, i.TotalPrice, i.OptionsText, i.Note))).ToArray();
        // Include full snapshot content so same-price item edits cannot reuse an obsolete quote.
        var fingerprint = BillIdentity.Hash(System.Text.Json.JsonSerializer.Serialize(new { Rounds = rounds, Lines = lines }));
        return new(session.TenantId, session.BranchId, session.Id, session.TableId, session.TableCode,
            fingerprint, live.Sum(o => o.OriginalAmount), live.Sum(o => o.DiscountAmount), live.Sum(o => o.FinalAmount), rounds, lines);
    }
}
