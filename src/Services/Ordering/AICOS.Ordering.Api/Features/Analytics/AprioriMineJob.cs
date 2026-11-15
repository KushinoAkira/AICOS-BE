using Microsoft.Extensions.Options;

namespace AICOS.Ordering.Api.Features.Analytics;

/// <summary>Periodically re-mines Apriori rules for every tenant that has recent paid baskets.</summary>
public sealed class AprioriMineJob(
    IServiceScopeFactory scopes,
    IOptions<AprioriOptions> options,
    ILogger<AprioriMineJob> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var hours = Math.Clamp(options.Value.MineIntervalHours, 1, 168);
        using var timer = new PeriodicTimer(TimeSpan.FromHours(hours));
        // First run after a short delay so Postgres migrations finish.
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var miner = scope.ServiceProvider.GetRequiredService<AprioriMiner>();
                var rules = await miner.MineAllTenantsAsync(stoppingToken);
                if (rules > 0)
                {
                    logger.LogInformation("Apriori mine job stored {RuleCount} rules across tenants.", rules);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Apriori mine job failed.");
            }
        }
        while (await WaitAsync(timer, stoppingToken));
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
