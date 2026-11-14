using AICOS.BuildingBlocks.Exceptions;
using AICOS.BuildingBlocks.Security;
using AICOS.Payments.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AICOS.Payments.Api.Features.SessionBilling;

public static class BillHistoryEndpoint
{
    public static async Task<IResult> GetAsync(Guid id, Guid? sessionId, ICurrentUser user,
        BillLedgerService ledger, PaymentsDbContext db, CancellationToken ct)
    {
        var tenant = TenantScope.Require(user);
        var exists = await db.SessionBills.AsNoTracking().AnyAsync(b => b.Id == id && b.TenantId == tenant, ct);
        var view = exists ? await ledger.GetByIdAsync(id, user, false, ct)
            : sessionId is {} session ? await ledger.GetAsync(session, user, ct)
            : throw new NotFoundException("Bill", id);
        if (view.Id != id || sessionId is {} expected && view.SessionId != expected)
            throw new ConflictException("Bill/session identity differs.", "billing.scope_mismatch");
        var attempts = await db.BillAttempts.AsNoTracking().Where(a => a.BillId == id && a.TenantId == tenant)
            .OrderByDescending(a => a.CreatedAtUtc).ThenBy(a => a.Id).Take(101).ToListAsync(ct);
        var events = await db.BillJournal.AsNoTracking().Where(e => e.BillId == id)
            .OrderByDescending(e => e.CreatedAtUtc).ThenBy(e => e.Id).Take(101)
            .Select(e => new { e.Id, e.Kind, e.CreatedAtUtc }).ToListAsync(ct);
        return Results.Ok(new { BillId = id, view.SessionId, view.BranchId,
            Attempts = attempts.Take(100).Select(BillView.View), Events = events.Take(100),
            HasMore = attempts.Count > 100 || events.Count > 100 });
    }
}
