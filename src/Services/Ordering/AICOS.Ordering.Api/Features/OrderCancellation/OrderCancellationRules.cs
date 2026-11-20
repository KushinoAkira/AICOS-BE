using System.Text.Json;
using AICOS.Contracts.Billing;
using AICOS.Contracts.Serving;
using AICOS.Ordering.Api.Domain.Entities;
using AICOS.Ordering.Api.Domain.Enums;
using AICOS.Ordering.Api.Features.Orders;
using AICOS.Ordering.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AICOS.Ordering.Api.Features.OrderCancellation;

public static class OrderCancellationRules
{
    public static string Snapshot(Order order)
    {
        var snapshot = OrderResponse.From(order);
        return JsonSerializer.Serialize(snapshot with { Items = snapshot.Items.OrderBy(x => x.Id)
            .Select(x => x with { Options = x.Options?.OrderBy(o => o.OptionId).ToArray() }).ToArray() });
    }
    public static string Fingerprint(Order order) => BillIdentity.Hash(Snapshot(order));
    public static string? Blocked(Order o) => o.FulfillmentMode != FulfillmentModes.WholeOrder ? "order.cup_cancellation_required"
        : o.Type != OrderType.DineIn || o.TableSessionId is null || o.TableId is null || o.PayFirst || string.IsNullOrWhiteSpace(o.TableNumber) ? "order.cancel_session_required"
        : o.PaymentStatus != PaymentStatus.Unpaid || o.IsVoided || o.DeliveredAtUtc != null
            || o.CompletedAtUtc != null || o.Status is not (OrderStatus.Pending or OrderStatus.Confirmed) || o.RemakeCount != 0
            ? "order.cancel_bar_review_required"
        : !string.IsNullOrWhiteSpace(o.PromotionCode) ? "order.cancel_promotion_review_required" : null;

    public static Task<bool> PendingSession(OrderingDbContext db, Guid session, CancellationToken ct) =>
        db.CancellationGuardEnabled ? db.Cancellations.AnyAsync(x => x.SessionId == session && x.CompletedAtUtc == null, ct) : Task.FromResult(false);

    internal static Task<T> Locked<T>(OrderingDbContext db, Guid tenant, Guid order, Guid table, Func<Task<T>> action, CancellationToken ct) =>
        db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var tx = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(ct) : null;
            if (tx is not null)
                foreach (var key in new[] { $"aicos:delivery:{tenant}:{order}", $"aicos:counter-table:{tenant}:{table}" })
                    await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({key}, 0))", ct);
            var result = await action();
            if (tx is not null) await tx.CommitAsync(ct);
            return result;
        });
}
