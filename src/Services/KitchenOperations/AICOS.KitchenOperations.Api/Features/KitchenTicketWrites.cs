using AICOS.KitchenOperations.Api.Domain.Entities;
using AICOS.KitchenOperations.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AICOS.KitchenOperations.Api.Features;

/// <summary>Serializes operational ticket writers by tenant/order across Kitchen replicas.</summary>
internal static class KitchenTicketWrites
{
    public static Task<T> RunAsync<T>(KitchenOperationsDbContext db, Guid tenant, Guid orderId,
        Func<Task<T>> mutation, CancellationToken ct) =>
        db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(ct) : null;
            if (transaction is not null)
            {
                var key = $"aicos:kitchen-operation:{tenant}:{orderId}";
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({key}, 0))", ct);
            }
            var result = await mutation();
            if (transaction is not null) await transaction.CommitAsync(ct);
            return result;
        });

    public static Task<KitchenTicket?> ApplyAsync(KitchenOperationsDbContext db, Guid tenant, Guid branch,
        Guid orderId, Func<KitchenTicket, bool> eligible, Action<KitchenTicket> mutate, CancellationToken ct) =>
        RunAsync<KitchenTicket?>(db, tenant, orderId, async () =>
        {
            var ticket = await TicketLookup.ByOrderAsync(db, tenant, orderId, ct);
            if (ticket is null || ticket.BranchId != branch || !eligible(ticket)) return null;
            if (ticket.FulfillmentMode != AICOS.Contracts.Serving.FulfillmentModes.WholeOrder)
            {
                ticket.HoldRecovery("cup.legacy_event_requires_review");
                await db.SaveChangesAsync(ct);
                return null;
            }
            mutate(ticket);
            await db.SaveChangesAsync(ct);
            return ticket;
        }, ct);
}
