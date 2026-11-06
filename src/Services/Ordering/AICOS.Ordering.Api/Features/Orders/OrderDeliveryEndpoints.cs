using AICOS.BuildingBlocks.Security;
using AICOS.Ordering.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AICOS.Ordering.Api.Features.Orders;

/// <summary>Serializes delivery confirmations across Ordering replicas without changing financial state.</summary>
public static class OrderDeliveryEndpoints
{
    public static async Task<OrderResponse> DeliverAsync(Guid id, OrderingDbContext db, ICurrentUser user,
        OrderEventPublisher events, ShopTime clock, CancellationToken ct)
    {
        var tenant = TenantScope.Require(user);
        var outcome = await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(ct) : null;
            if (transaction is not null)
            {
                var key = $"aicos:delivery:{tenant}:{id}";
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({key}, 0))", ct);
            }
            var order = await OrderAccess.LoadAsync(db, user, id, ct);
            var replay = order.DeliveredAtUtc is not null && !order.IsVoided;
            if (!replay)
            {
                order.Deliver(user.UserId, clock.UtcNow);
                await db.SaveChangesAsync(ct);
            }
            if (transaction is not null) await transaction.CommitAsync(ct);
            return (Order: order, Replay: replay);
        });

        // A lost response can be retried to repair a missed operational notification.
        await events.DeliveredAsync(outcome.Order, ct);
        if (!outcome.Replay)
        {
            if (outcome.Order.Status == Domain.Enums.OrderStatus.Completed)
                await events.CompletedAsync(outcome.Order, ct);
            else
                await events.ChangedAsync(outcome.Order, "Delivered", ct);
        }
        return OrderResponse.From(outcome.Order);
    }
}
