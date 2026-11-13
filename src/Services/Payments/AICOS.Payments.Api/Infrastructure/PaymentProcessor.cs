using AICOS.BuildingBlocks.Messaging;
using AICOS.Contracts.Events;
using AICOS.Payments.Api.Domain;
using AICOS.Payments.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AICOS.Payments.Api.Infrastructure;

/// <summary>
/// The single place where a payment changes state because of what the provider (or the cashier) reported, so the webhook, the status
/// query, the simulate endpoint and the expiry job all behave the same and publish the same events.
/// </summary>
public sealed class PaymentProcessor(PaymentsDbContext db, IEventBus bus, TimeProvider clock)
{
    public DateTime UtcNow => clock.GetUtcNow().UtcDateTime;

    /// <summary>The provider says the money arrived. Returns "applied", "applied_late" or "ignored_final" (already paid).</summary>
    public async Task<string> ApplySuccessAsync(Payment payment, string? providerReference, CancellationToken ct)
    {
        var appliedInThisCall = false;
        var result = await PaymentOrderLock.RunAsync(db, payment.TenantId, payment.OrderId, async () =>
        {
            // Other requests may have completed this attempt while this request waited for the lock.
            if (db.Database.IsRelational()) await db.Entry(payment).ReloadAsync(ct);
            if (payment.IsPaid)
                return appliedInThisCall ? (payment.PaidLate ? "applied_late" : "applied") : "ignored_final";
            payment.SetProviderState(ProviderState.Succeeded);
            payment.MarkSucceeded(providerReference, UtcNow);
            await db.SaveChangesAsync(ct);
            appliedInThisCall = true;
            return payment.PaidLate ? "applied_late" : "applied";
        }, ct);
        if (result is "applied" or "applied_late") await PublishSucceededAsync(payment, ct);
        return result;
    }
    /// <summary>The provider says the payment failed. A payment that is already final is left alone.</summary>
    public async Task<string> ApplyFailureAsync(Payment payment, string reason, CancellationToken ct)
    {
        var result = await PaymentOrderLock.RunAsync(db, payment.TenantId, payment.OrderId, async () =>
        {
            if (db.Database.IsRelational()) await db.Entry(payment).ReloadAsync(ct);
            if (payment.IsPaid || (payment.IsFinal && payment.ProviderState == ProviderState.Failed)) return "ignored_final";
            payment.SetProviderState(ProviderState.Failed);
            if (payment.IsFinal)
            {
                // Resolve the provider uncertainty; retain the historical local final status.
                await db.SaveChangesAsync(ct);
                return "resolved_failed";
            }
            payment.MarkFailed(reason);
            await db.SaveChangesAsync(ct);
            return "applied";
        }, ct);
        if (result == "applied") await PublishFailedAsync(payment, reason, ct);
        return result;
    }

    /// <summary>Expiry and success share a lock; a stale timer can never overwrite received money.</summary>
    public async Task<bool> ExpireIfDueAsync(Payment payment, CancellationToken ct)
    {
        var expired = await PaymentOrderLock.RunAsync(db, payment.TenantId, payment.OrderId, async () =>
        {
            if (db.Database.IsRelational()) await db.Entry(payment).ReloadAsync(ct);
            if (!payment.IsDue(UtcNow)) return false;
            payment.MarkExpired();
            await db.SaveChangesAsync(ct);
            return true;
        }, ct);
        // A local deadline is not proof of failure at the provider. Keep reconciliation
        // blocking and never cancel the order by publishing a timed-out failure here.
        return expired;
    }

    public async Task<int> ExpireDueAsync(CancellationToken ct)
    {
        var now = UtcNow;
        var due = await db.Payments.Where(p => p.Status == PaymentStatus.Pending && p.ExpiresAtUtc != null && p.ExpiresAtUtc <= now).ToListAsync(ct);
        var expired = 0;
        foreach (var payment in due) if (await ExpireIfDueAsync(payment, ct)) expired++;
        return expired;
    }
    public Task PublishSucceededAsync(Payment p, CancellationToken ct) =>
        bus.PublishAsync(new PaymentSucceededEvent(p.TenantId, p.BranchId, p.Id, p.OrderId, p.Amount, p.Method, p.PaidAtUtc ?? UtcNow), ct);

    public Task PublishFailedAsync(Payment p, string reason, CancellationToken ct, bool timedOut = false) =>
        bus.PublishAsync(new PaymentFailedEvent(p.TenantId, p.BranchId, p.Id, p.OrderId, p.Method, reason, timedOut), ct);
}

/// <summary>Marks local payment deadlines without releasing collection or declaring provider failure.</summary>
public sealed class PaymentExpiryJob(IServiceScopeFactory scopes, Microsoft.Extensions.Options.IOptions<PaymentOptions> options, ILogger<PaymentExpiryJob> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(5, options.Value.ExpirySweepSeconds));
        using var timer = new PeriodicTimer(interval);
        while (await WaitAsync(timer, stoppingToken))
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var expired = await scope.ServiceProvider.GetRequiredService<PaymentProcessor>().ExpireDueAsync(stoppingToken);
                if (expired > 0)
                {
                    logger.LogInformation("Expired {Count} unpaid payments.", expired);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Expiring unpaid payments failed.");
            }
        }
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            return await timer.WaitForNextTickAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
