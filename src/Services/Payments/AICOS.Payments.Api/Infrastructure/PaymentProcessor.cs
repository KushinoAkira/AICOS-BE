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
        if (payment.IsPaid)
        {
            return "ignored_final";
        }

        payment.SetProviderState(ProviderState.Succeeded);
        payment.MarkSucceeded(providerReference, UtcNow);
        await db.SaveChangesAsync(ct);
        await PublishSucceededAsync(payment, ct);
        return payment.PaidLate ? "applied_late" : "applied";
    }

    /// <summary>The provider says the payment failed. A payment that is already final is left alone.</summary>
    public async Task<string> ApplyFailureAsync(Payment payment, string reason, CancellationToken ct)
    {
        if (payment.IsFinal)
        {
            return "ignored_final";
        }

        payment.SetProviderState(ProviderState.Failed);
        payment.MarkFailed(reason);
        await db.SaveChangesAsync(ct);
        await PublishFailedAsync(payment, reason, ct);
        return "applied";
    }

    /// <summary>Marks a pending payment expired when its time is up. Returns true when it changed.</summary>
    public async Task<bool> ExpireIfDueAsync(Payment payment, CancellationToken ct)
    {
        if (!payment.IsDue(UtcNow))
        {
            return false;
        }

        payment.MarkExpired();
        await db.SaveChangesAsync(ct);
        await PublishFailedAsync(payment, "Not paid in time.", ct);
        return true;
    }

    /// <summary>Background sweep: expires every pending payment whose time is up.</summary>
    public async Task<int> ExpireDueAsync(CancellationToken ct)
    {
        var now = UtcNow;
        var due = await db.Payments.Where(p => p.Status == PaymentStatus.Pending && p.ExpiresAtUtc != null && p.ExpiresAtUtc <= now).ToListAsync(ct);
        foreach (var payment in due)
        {
            payment.MarkExpired();
        }

        await db.SaveChangesAsync(ct);
        foreach (var payment in due)
        {
            await PublishFailedAsync(payment, "Not paid in time.", ct);
        }

        return due.Count;
    }

    public Task PublishSucceededAsync(Payment p, CancellationToken ct) =>
        bus.PublishAsync(new PaymentSucceededEvent(p.TenantId, p.BranchId, p.Id, p.OrderId, p.Amount, p.Method, p.PaidAtUtc ?? UtcNow), ct);

    public Task PublishFailedAsync(Payment p, string reason, CancellationToken ct) =>
        bus.PublishAsync(new PaymentFailedEvent(p.TenantId, p.BranchId, p.Id, p.OrderId, p.Method, reason), ct);
}

/// <summary>Expires unpaid online payments on a timer so the held order slot is released even when nobody asks about the payment (BR-25, BR-60).</summary>
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
