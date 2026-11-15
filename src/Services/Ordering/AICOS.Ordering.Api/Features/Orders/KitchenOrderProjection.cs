using AICOS.Ordering.Api.Domain.Entities;
using AICOS.Ordering.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AICOS.Ordering.Api.Features.Orders;

/// <summary>Read and transition under the delivery/table locks, not only at SaveChanges.</summary>
internal static class KitchenOrderProjection
{
    public static Task<(Order? Order, bool Changed)> ApplyAsync(OrderingDbContext db, Guid tenant, Guid id, Action<Order> transition, CancellationToken ct) =>
        db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            for (var attempt = 0; ; attempt++)
            {
                db.ChangeTracker.Clear();
                await using var transaction = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(ct) : null;
                if (transaction is not null)
                {
                    var orderKey = $"aicos:delivery:{tenant}:{id}";
                    await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({orderKey}, 0))", ct);
                    var table = await db.Orders.AsNoTracking().Where(o => o.Id == id && o.TenantId == tenant).Select(o => o.TableId).FirstOrDefaultAsync(ct);
                    if (table is not null)
                    {
                        var tableKey = $"aicos:counter-table:{tenant}:{table}";
                        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({tableKey}, 0))", ct);
                        var latest = await db.Orders.AsNoTracking().Where(o => o.Id == id && o.TenantId == tenant).Select(o => o.TableId).FirstOrDefaultAsync(ct);
                        if (latest != table)
                        {
                            // Transfer can commit before the old table lock is acquired; retry from a fresh snapshot.
                            if (attempt >= 3) throw new AICOS.BuildingBlocks.Exceptions.ConflictException("Table moved during Kitchen projection.", "qr.session_changed");
                            continue;
                        }
                    }
                }
                var order = await OrderLookup.FindAsync(db, tenant, id, ct);
                if (order is null) return ((Order?)null, false);
                var before = (order.Status, order.RemakeCount, order.ReadyAtUtc);
                transition(order);
                await db.SaveChangesAsync(ct);
                if (transaction is not null) await transaction.CommitAsync(ct);
                return (order, before != (order.Status, order.RemakeCount, order.ReadyAtUtc));
            }
        });
}
