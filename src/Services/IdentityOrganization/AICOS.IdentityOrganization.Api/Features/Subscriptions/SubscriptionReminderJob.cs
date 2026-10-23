using AICOS.BuildingBlocks.Messaging;
using AICOS.Contracts.Events;
using AICOS.IdentityOrganization.Api.Domain;
using AICOS.IdentityOrganization.Api.Infrastructure;
using AICOS.IdentityOrganization.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AICOS.IdentityOrganization.Api.Features.Subscriptions;

public sealed record ReminderRunResult(int RenewalNotices, int OverdueNotices);

/// <summary>
/// FR-05 / BR-10: tells brands their renewal date is near, or that they did not renew. Publishes an event per tenant and
/// billing period (never repeated); the Notification service turns the events into messages for the Brand Admin.
/// Suspended tenants are skipped: they cannot act on a notice anyway.
/// </summary>
public sealed class SubscriptionReminderJob(
    IdentityOrganizationDbContext db,
    IEventBus bus,
    IOptions<SubscriptionOptions> options,
    TimeProvider clock,
    ILogger<SubscriptionReminderJob> logger)
{
    public async Task<ReminderRunResult> RunAsync(CancellationToken ct)
    {
        var settings = options.Value;
        var now = clock.GetUtcNow().UtcDateTime;
        int renewal = 0, overdue = 0;

        var rows = await (from s in db.TenantSubscriptions
                          join t in db.Tenants on s.TenantId equals t.Id // no AsNoTracking here: it would stop EF tracking the subscriptions we mark as notified
                          where s.Status == SubscriptionStatus.Active && t.Status == TenantStatus.Active
                          select new { Sub = s, TenantName = t.Name }).ToListAsync(ct);

        foreach (var row in rows)
        {
            var sub = row.Sub;
            var health = sub.HealthAt(now, settings.RenewalNoticeDays, settings.GracePeriodDays);

            try
            {
                if (health == SubscriptionHealth.DueSoon && sub.RenewalNoticeForPeriodEndUtc != sub.PeriodEndUtc)
                {
                    await bus.PublishAsync(
                        new SubscriptionRenewalDueEvent(sub.TenantId, row.TenantName, sub.PlanName, sub.PeriodEndUtc, TenantSubscriptionEndpoints.DaysUntil(sub.PeriodEndUtc, now)),
                        ct);
                    sub.MarkRenewalNoticeSent();
                    renewal++;
                }
                else if (health is SubscriptionHealth.GracePeriod or SubscriptionHealth.Overdue && sub.OverdueNoticeForPeriodEndUtc != sub.PeriodEndUtc)
                {
                    await bus.PublishAsync(
                        new SubscriptionOverdueEvent(
                            sub.TenantId, row.TenantName, sub.PlanName, sub.PeriodEndUtc, (int)Math.Floor((now - sub.PeriodEndUtc).TotalDays),
                            GracePeriodOver: health == SubscriptionHealth.Overdue),
                        ct);
                    sub.MarkOverdueNoticeSent();
                    overdue++;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Not marked as sent, so the next run tries again.
                logger.LogWarning(ex, "Could not publish the subscription notice for tenant {TenantId}", sub.TenantId);
            }
        }

        await db.SaveChangesAsync(ct);
        return new ReminderRunResult(renewal, overdue);
    }
}

/// <summary>Runs <see cref="SubscriptionReminderJob"/> every <c>Subscriptions:ReminderIntervalMinutes</c> (0 = off).</summary>
public sealed class SubscriptionReminderService(IServiceScopeFactory scopes, IOptions<SubscriptionOptions> options, ILogger<SubscriptionReminderService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var minutes = options.Value.ReminderIntervalMinutes;
        if (minutes <= 0)
        {
            return;
        }

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); // let migrations finish first
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(minutes));
            do
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    var result = await scope.ServiceProvider.GetRequiredService<SubscriptionReminderJob>().RunAsync(stoppingToken);
                    logger.LogInformation(
                        "Subscription reminders: {Renewal} renewal notice(s), {Overdue} overdue notice(s)", result.RenewalNotices, result.OverdueNotices);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Subscription reminder run failed; will retry on the next tick");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }
}
