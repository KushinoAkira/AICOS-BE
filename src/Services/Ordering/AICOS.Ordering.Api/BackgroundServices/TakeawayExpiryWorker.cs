using AICOS.Ordering.Api.Services.Takeaway;

namespace AICOS.Ordering.Api.BackgroundServices;

/// <summary>SCRUM-121: cancel unpaid takeaway orders and mark no-shows once per minute.</summary>
public sealed class TakeawayExpiryWorker(IServiceProvider services, ILogger<TakeawayExpiryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        await RunOnce(stoppingToken);
        while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken))
        {
            await RunOnce(stoppingToken);
        }
    }

    private async Task RunOnce(CancellationToken stoppingToken)
    {
        try
        {
            using var scope = services.CreateScope();
            var pickup = scope.ServiceProvider.GetRequiredService<PickupOrderService>();
            await pickup.RunExpiryJobAsync(stoppingToken);
        }
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
        {
            logger.LogError(ex, "Takeaway expiry job failed.");
        }
    }
}
