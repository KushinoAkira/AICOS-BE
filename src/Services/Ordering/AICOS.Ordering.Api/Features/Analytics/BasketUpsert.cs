using AICOS.Ordering.Api.Domain.Entities;
using AICOS.Ordering.Api.Domain.Enums;
using AICOS.Ordering.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AICOS.Ordering.Api.Features.Analytics;

/// <summary>Copies a paid order into <see cref="OrderBasket"/> for Apriori (idempotent by order id).</summary>
public static class BasketUpsert
{
    public static async Task FromOrderAsync(OrderingDbContext db, Order order, CancellationToken ct)
    {
        if (order.PaymentStatus != PaymentStatus.Paid || order.PaidAtUtc is null || order.Items.Count == 0)
        {
            return;
        }

        var existing = await db.OrderBaskets.Include(b => b.Items).FirstOrDefaultAsync(b => b.Id == order.Id, ct);
        if (existing is not null)
        {
            return;
        }

        var basket = new OrderBasket(order.Id, order.TenantId, order.BranchId, order.CustomerId, order.PaidAtUtc.Value, order.FinalAmount);
        basket.ReplaceItems(order.Items.Select(i => new OrderBasketItem(order.Id, i.ProductId, i.ProductName, i.Quantity, i.TotalPrice)));
        db.OrderBaskets.Add(basket);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Backfill paid orders that are not yet in the basket table (used by the mine job).</summary>
    public static async Task<int> BackfillAsync(OrderingDbContext db, Guid? tenantId, DateTime sinceUtc, CancellationToken ct)
    {
        var query = db.Orders.AsNoTracking()
            .Include(o => o.Items)
            .Where(o => o.PaymentStatus == PaymentStatus.Paid && o.PaidAtUtc != null && o.PaidAtUtc >= sinceUtc);
        if (tenantId is not null)
        {
            query = query.Where(o => o.TenantId == tenantId);
        }

        var orders = await query.ToListAsync(ct);
        if (orders.Count == 0)
        {
            return 0;
        }

        var ids = orders.Select(o => o.Id).ToList();
        var knownSet = (await db.OrderBaskets.AsNoTracking()
            .Where(b => ids.Contains(b.Id))
            .Select(b => b.Id)
            .ToListAsync(ct)).ToHashSet();
        var added = 0;
        foreach (var order in orders.Where(o => !knownSet.Contains(o.Id) && o.Items.Count > 0))
        {
            var basket = new OrderBasket(order.Id, order.TenantId, order.BranchId, order.CustomerId, order.PaidAtUtc!.Value, order.FinalAmount);
            basket.ReplaceItems(order.Items.Select(i => new OrderBasketItem(order.Id, i.ProductId, i.ProductName, i.Quantity, i.TotalPrice)));
            db.OrderBaskets.Add(basket);
            added++;
        }

        if (added > 0)
        {
            await db.SaveChangesAsync(ct);
        }

        return added;
    }
}
