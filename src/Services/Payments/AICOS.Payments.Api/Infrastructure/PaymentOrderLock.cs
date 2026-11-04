using System.Data;
using AICOS.Payments.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AICOS.Payments.Api.Infrastructure;

/// <summary>One PostgreSQL transaction/retry boundary per tenant/order, shared by cash and provider success.</summary>
public static class PaymentOrderLock
{
    public static Task<T> RunAsync<T>(PaymentsDbContext db, Guid tenant, Guid order, Func<Task<T>> action, CancellationToken ct) =>
        db.Database.CurrentTransaction is not null ? action() : db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            if (!db.Database.IsRelational()) return await action();
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
            var key = $"aicos:payment-order:{tenant}:{order}";
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({key}, 0))", ct);
            var result = await action();
            await transaction.CommitAsync(ct);
            return result;
        });
}
