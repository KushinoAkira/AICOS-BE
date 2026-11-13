using AICOS.Payments.Api.Persistence;
using Microsoft.EntityFrameworkCore;
namespace AICOS.Payments.Api.Features.SessionBilling;

internal static class BillLock
{
    public static Task<T> RunAsync<T>(PaymentsDbContext db, Guid bill, Func<Task<T>> action, CancellationToken ct, string? key = null) =>
        db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            if (!db.Database.IsRelational()) return await action();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            if (key is not null) await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"aicos:bill-request:" + key}, 0))", ct);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"aicos:bill:" + bill}, 0))", ct);
            var result = await action();
            await tx.CommitAsync(ct);
            return result;
        });
}
