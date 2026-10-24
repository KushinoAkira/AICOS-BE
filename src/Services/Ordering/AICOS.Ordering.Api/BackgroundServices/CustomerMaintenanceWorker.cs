using AICOS.Ordering.Api.Services.Takeaway;

namespace AICOS.Ordering.Api.BackgroundServices;

/// <summary>SCRUM-83 / 122: analyze feedback and anonymize due deletion requests daily.</summary>
public sealed class CustomerMaintenanceWorker(IServiceProvider services, ILogger<CustomerMaintenanceWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(24));
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
            var profiles = scope.ServiceProvider.GetRequiredService<CustomerProfileService>();
            await profiles.AnalyzePendingFeedbackAsync(stoppingToken);
            await profiles.ProcessDueDeletionRequestsAsync(stoppingToken);
        }
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
        {
            logger.LogError(ex, "Customer maintenance job failed.");
        }
    }
}
