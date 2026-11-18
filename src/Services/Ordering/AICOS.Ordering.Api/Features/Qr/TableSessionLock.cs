using System.Data;
using AICOS.Ordering.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AICOS.Ordering.Api.Features.Qr;

/// <summary>Same table lock as POS create and transfer. Recheck session state after acquiring it.</summary>
internal static class TableSessionLock
{
    public static Task<T> RunAsync<T>(OrderingDbContext db, Guid tenant, Guid table, Func<Task<T>> action, CancellationToken ct) =>
        db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            if (!db.Database.IsRelational()) return await action();
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
            var key = $"aicos:counter-table:{tenant}:{table}";
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({key}, 0))", ct);
            var result = await action();
            await transaction.CommitAsync(ct);
            return result;
        });
}
