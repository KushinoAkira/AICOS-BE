using StackExchange.Redis;

namespace AICOS.BuildingBlocks.Messaging;

/// <summary>One subscribed event type + the delegate that deserialises the payload and runs all its handlers.</summary>
internal sealed record EventSubscription(string Channel, Func<IServiceProvider, string, CancellationToken, Task> Dispatch);

/// <summary>Subscribes to every channel that has a registered handler and dispatches messages to handlers.</summary>
internal sealed class RedisEventBusSubscriber(
    RedisConnectionProvider redis,
    IEnumerable<EventSubscription> subscriptions,
    IServiceScopeFactory scopeFactory,
    ILogger<RedisEventBusSubscriber> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var distinct = subscriptions.DistinctBy(s => s.Channel).ToList();
        if (distinct.Count == 0)
        {
            return;
        }

        // Do not block host start-up while connecting.
        await Task.Yield();

        var connection = await redis.GetAsync();
        var subscriber = connection.GetSubscriber();

        foreach (var subscription in distinct)
        {
            await subscriber.SubscribeAsync(
                RedisChannel.Literal(subscription.Channel),
                (channel, message) => { _ = HandleAsync(subscription, message.ToString(), stoppingToken); });

            logger.LogInformation("Subscribed to {Channel}", subscription.Channel);
        }
    }

    private async Task HandleAsync(EventSubscription subscription, string payload, CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            await subscription.Dispatch(scope.ServiceProvider, payload, cancellationToken);
        }
        catch (Exception ex)
        {
            // Pub/Sub is fire-and-forget: a failed handler is logged, not retried.
            logger.LogError(ex, "Handler for {Channel} failed", subscription.Channel);
        }
    }
}
