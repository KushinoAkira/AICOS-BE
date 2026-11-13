using System.Text.Json;
using AICOS.Contracts.Serving;
using AICOS.KitchenOperations.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AICOS.KitchenOperations.Api.Features.CupServing;

/// <summary>Per-row next_attempt_at is a durable reconciliation cursor, including ACKED rows of every age.</summary>
public sealed class FulfillmentDispatcher(KitchenOperationsDbContext db, IFulfillmentTransport transport)
{
    public async Task<int> DispatchOnceAsync(CancellationToken ct = default)
    {
        var rows = await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var batch = await db.FulfillmentOutbox.FromSqlRaw("SELECT * FROM kitchen.fulfillment_outbox WHERE (state IN ('PENDING','ACKED') AND next_attempt_at_utc <= clock_timestamp()) OR (state = 'IN_FLIGHT' AND lease_until_utc <= clock_timestamp()) ORDER BY next_attempt_at_utc, event_id LIMIT 20 FOR UPDATE SKIP LOCKED").ToListAsync(ct);
            var now = await db.Database.SqlQuery<DateTime>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync(ct);
            foreach (var row in batch)
            {
                row.State = "IN_FLIGHT"; row.LeaseToken = Guid.NewGuid(); row.LeaseUntilUtc = now.AddSeconds(30);
                row.AcknowledgedAtUtc = null; row.AttemptCount++;
            }
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return batch;
        });
        db.ChangeTracker.Clear();
        var count = 0;
        foreach (var row in rows)
        {
            // A later row in the batch may already have expired while earlier HTTP calls ran.
            var leaseNow = await db.Database.SqlQuery<DateTime>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync(ct);
            if (await db.FulfillmentOutbox.Where(r => r.EventId == row.EventId && r.State == "IN_FLIGHT"
                && r.LeaseToken == row.LeaseToken && r.LeaseUntilUtc > leaseNow).ExecuteUpdateAsync(s => s
                    .SetProperty(r => r.LeaseUntilUtc, leaseNow.AddSeconds(30)), ct) != 1) continue;
            var state = "PENDING"; string? error = "fulfillment.transport";
            try
            {
                var snapshot = JsonSerializer.Deserialize<FulfillmentSnapshot>(row.Payload)!;
                if (snapshot is null || snapshot.EventId != row.EventId || snapshot.OrderId != row.OrderId || snapshot.TenantId != row.TenantId
                    || snapshot.BranchId != row.BranchId || snapshot.Version != row.SourceVersion
                    || FulfillmentSnapshotHash.Compute(snapshot) != row.PayloadHash)
                { state = "NEEDS_REVIEW"; error = "fulfillment.corrupt_outbox"; }
                else
                {
                    var result = await transport.SendAsync(snapshot, ct); // no database transaction held
                    var ack = result.Ack;
                    if (result.Status == 200 && ack is not null && ack.EventId == row.EventId && ack.OrderId == row.OrderId
                        && ack.TenantId == row.TenantId && ack.BranchId == row.BranchId && ack.Version == row.SourceVersion
                        && ack.PayloadHash == row.PayloadHash && ack.Outcome is "Applied" or "Duplicate" or "Superseded")
                    { state = "ACKED"; error = null; }
                    else if (result.Status is 400 or 409 or 422) { state = "NEEDS_REVIEW"; error = "fulfillment.conflict"; }
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            { if (ct.IsCancellationRequested) throw; }
            var now = await db.Database.SqlQuery<DateTime>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync(ct);
            var next = now.AddSeconds(state == "ACKED" ? 60 : Math.Min(60, Math.Pow(2, Math.Min(row.AttemptCount, 5))));
            DateTime? ackAt = state == "ACKED" ? now : null;
            var changed = await db.FulfillmentOutbox.Where(r => r.EventId == row.EventId && r.State == "IN_FLIGHT"
                && r.LeaseToken == row.LeaseToken && r.LeaseUntilUtc > now).ExecuteUpdateAsync(s => s
                    .SetProperty(r => r.State, state).SetProperty(r => r.LeaseToken, (Guid?)null)
                    .SetProperty(r => r.LeaseUntilUtc, (DateTime?)null).SetProperty(r => r.NextAttemptAtUtc, next)
                    .SetProperty(r => r.AcknowledgedAtUtc, ackAt).SetProperty(r => r.LastErrorCode, error), ct);
            if (changed == 1 && state == "ACKED") count++;
        }
        return count;
    }
}

public sealed class FulfillmentWorker(IServiceScopeFactory scopes, IConfiguration config, ILogger<FulfillmentWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!config.GetValue<bool>("CupServing:DispatcherEnabled")) return;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<FulfillmentDispatcher>().DispatchOnceAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning("Cup fulfillment retry deferred ({Type}); durable rows retained.", ex.GetType().Name); }
            try { await Task.Delay(TimeSpan.FromSeconds(2), ct); } catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
        }
    }
}
