using AICOS.BuildingBlocks.Exceptions;
using AICOS.Ordering.Api.Domain.Enums;
using AICOS.Ordering.Api.Domain.Entities;
using AICOS.Ordering.Api.Domain;
using AICOS.Ordering.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AICOS.Ordering.Api.Features.SessionBilling;

internal static class SessionBillingWriteGuard
{
    public static async Task CheckAsync(OrderingDbContext db, CancellationToken ct)
    {
        db.ChangeTracker.DetectChanges();
        var changedSessions = db.ChangeTracker.Entries<TableSession>().Where(x => x.State == EntityState.Deleted
            || x.State == EntityState.Modified && new[] { "TableId", "TableCode", "Status" }.Any(n => x.Property(n).IsModified)).ToList();
        var entries = db.ChangeTracker.Entries<Order>().Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted).ToList();
        var dirtyItems = db.ChangeTracker.Entries<OrderItem>().Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted).Select(e => e.Entity.OrderId).ToHashSet();
        foreach (var id in dirtyItems.Where(id => entries.All(e => e.Entity.Id != id)))
        {
            var order = await db.Orders.FirstOrDefaultAsync(o => o.Id == id, ct);
            if (order is not null) entries.Add(db.Entry(order));
        }
        if (db.Database.IsRelational())
        {
            var locks = entries.SelectMany(e => new[] { (e.Entity.TenantId, e.Entity.TableId),
                (e.Entity.TenantId, e.State == EntityState.Added ? null : e.OriginalValues.GetValue<Guid?>("TableId")) })
                .Concat(changedSessions.SelectMany(e => new[] { (e.Entity.TenantId, (Guid?)e.Entity.TableId),
                    (e.Entity.TenantId, (Guid?)e.OriginalValues.GetValue<Guid>("TableId")) }))
                .Where(x => x.Item2 is not null).Distinct().OrderBy(x => x.TenantId).ThenBy(x => x.Item2);
            foreach (var pair in locks)
            {
                var key = $"aicos:counter-table:{pair.TenantId}:{pair.Item2}";
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({key}, 0))", ct);
            }
        }
        if (db.CancellationGuardEnabled)
            foreach (var entry in changedSessions)
                if (await db.Cancellations.AnyAsync(x => x.SessionId == entry.Entity.Id && x.CompletedAtUtc == null, ct))
                    throw new ConflictException("Resolve cancellation before moving or closing the session.", "order.cancel_pending");
        foreach (var entry in entries)
        {
            var o = entry.Entity;
            var priorSession = entry.State == EntityState.Added ? null : entry.OriginalValues.GetValue<Guid?>("TableSessionId");
            if (o.Type != OrderType.DineIn && o.TableSessionId is null && priorSession is null) continue;
            if (entry.State == EntityState.Added && o.TableSessionId is null)
            {
                // Legacy staff requests may join a known open session, but cannot create an unscoped financial order.
                var open = await db.TableSessions.AsNoTracking().FirstOrDefaultAsync(s => s.TenantId == o.TenantId
                    && s.BranchId == o.BranchId && s.TableId == o.TableId && s.Status == TableSessionStatus.Open, ct);
                if (open is null) throw new ConflictException("Use a verified open table session.", "billing.session_required");
                o.AttachToTableSession(open.Id);
            }
            if ((o.TableSessionId ?? priorSession) is not { } sid) continue;
            // The first POS round commits its new session and order together; existing sessions must still be re-read under the table lock.
            var pendingSession = entry.State == EntityState.Added
                ? db.ChangeTracker.Entries<TableSession>().FirstOrDefault(s => s.State == EntityState.Added
                    && s.Entity.Id == sid && s.Entity.TenantId == o.TenantId
                    && s.Entity.BranchId == o.BranchId && s.Entity.TableId == o.TableId)?.Entity
                : null;
            var session = pendingSession ?? await db.TableSessions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == sid && s.TenantId == o.TenantId, ct);
            if (session is null || !session.IsOpen)
                throw new ConflictException("The table session closed or changed.", "qr.session_changed");
            var financial = entry.State is EntityState.Added or EntityState.Deleted || dirtyItems.Contains(o.Id)
                || new[] { "DiscountAmount", "PromotionCode", "TableSessionId", "Type", "TableId", "TableNumber" }.Any(n => entry.Property(n).IsModified)
                || (entry.Property("Status").IsModified && o.IsVoided);
            if (db.CancellationGuardEnabled && financial && await db.Cancellations.AnyAsync(x =>
                x.SessionId == sid && x.CompletedAtUtc == null && x.Id != db.ApplyingCancellation, ct))
                throw new ConflictException("Resolve the pending cancellation before changing the bill.", "order.cancel_pending");
            var fence = await db.SessionCheckouts.AsNoTracking().FirstOrDefaultAsync(x => x.SessionId == sid, ct);
            if (fence is null) continue;
            if (financial || fence.State == "Closed")
                throw new ConflictException("The session bill is frozen for collection or already paid. Do not mutate its orders.", "billing.session_frozen");
        }
    }
}
