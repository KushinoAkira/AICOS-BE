using System.Text.Json;
using AICOS.BuildingBlocks.Security;
using AICOS.Contracts.Billing;
using AICOS.Payments.Api.Persistence;
using Microsoft.EntityFrameworkCore;
namespace AICOS.Payments.Api.Features.SessionBilling;

public sealed class BillOutboxDispatcher(PaymentsDbContext db, IBillOrderingClient ordering, IBillKitchenClient kitchen, TimeProvider time, ILogger<BillOutboxDispatcher> logger)
{
    public async Task TryFastAsync(Guid id, CancellationToken ct)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(5));
        try { await TryBillAsync(id, budget.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { /* Durable lease/outbox retries after the fast response budget. */ }
    }
    public async Task<bool> TryBillAsync(Guid id, CancellationToken ct)
    {
        try { return await DispatchAsync(id, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { logger.LogWarning("Bill synchronization will retry for {BillId} ({ErrorType}).", id, ex.GetType().Name); return false; }
    }
    private async Task<bool> DispatchAsync(Guid id, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime; var owner = Guid.NewGuid();
        var claim = await BillLock.RunAsync(db, id, async () =>
        {
            db.ChangeTracker.Clear();
            var bill = await db.SessionBills.SingleAsync(b => b.Id == id, ct);
            if (bill.NeedsReconciliation) return null;
            var row = await db.BillJournal.FirstOrDefaultAsync(e => e.BillId == id && e.Kind == "PAID" && e.DeliveredAtUtc == null
                && e.NextAttemptAtUtc <= now && (e.LeaseUntilUtc == null || e.LeaseUntilUtc <= now), ct);
            if (row is null) return null;
            row.LeaseOwner = owner; row.Fence++; row.LeaseUntilUtc = now.AddSeconds(30);
            await db.SaveChangesAsync(ct);
            return new Lease(row.Id, row.Fence, JsonSerializer.Deserialize<BillPaidNotice>(row.Payload)!, JsonSerializer.Deserialize<SessionBillQuote>(bill.QuoteJson)!.Orders.Select(o => o.OrderId).ToArray());
        }, ct);
        if (claim is null) return false;
        // No PostgreSQL transaction held across network I/O.
        var ack = await ordering.PaidAsync(claim.Notice, ct);
        if (ack) ack = await kitchen.PaidAsync(new(claim.Notice, claim.Orders), ct);
        return await BillLock.RunAsync(db, id, async () =>
        {
            db.ChangeTracker.Clear();
            var row = await db.BillJournal.SingleAsync(e => e.Id == claim.Id, ct);
            if (row.LeaseOwner != owner || row.Fence != claim.Fence) return false;
            row.LeaseOwner = null; row.LeaseUntilUtc = null;
            row.NextAttemptAtUtc = time.GetUtcNow().UtcDateTime.AddSeconds(Math.Min(120, Math.Pow(2, Math.Min(6, row.Fence))));
            if (ack)
            {
                row.DeliveredAtUtc = time.GetUtcNow().UtcDateTime;
                var bill = await db.SessionBills.SingleAsync(b => b.Id == id, ct);
                bill.OrderingAcknowledged = true;
            }
            await db.SaveChangesAsync(ct); return ack;
        }, ct);
    }
    private sealed record Lease(Guid Id, long Fence, BillPaidNotice Notice, IReadOnlyList<Guid> Orders);
}
public sealed class BillOutboxWorker(IServiceScopeFactory scopes, IConfiguration config, ILogger<BillOutboxWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        do
        {
            if (BillingMachineAuth.Enabled(config) && config.GetValue("SessionBilling:DispatcherEnabled", true))
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>(); var now = DateTime.UtcNow;
                    var bills = await db.BillJournal.AsNoTracking().Where(e => e.Kind == "PAID" && e.DeliveredAtUtc == null
                        && e.NextAttemptAtUtc <= now && (e.LeaseUntilUtc == null || e.LeaseUntilUtc <= now))
                        .OrderBy(e => e.CreatedAtUtc).Select(e => e.BillId).Take(20).ToListAsync(stoppingToken);
                    foreach (var id in bills) await scope.ServiceProvider.GetRequiredService<BillOutboxDispatcher>().TryBillAsync(id, stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                { logger.LogWarning("Bill outbox scan will retry ({ErrorType}).", ex.GetType().Name); }
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
