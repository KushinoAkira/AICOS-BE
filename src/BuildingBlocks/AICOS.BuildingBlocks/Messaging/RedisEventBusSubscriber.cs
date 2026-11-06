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

        var registered = new HashSet<string>();
        var callbacks = distinct.ToDictionary(s => s.Channel,
            s => new Action<RedisChannel, RedisValue>((channel, message) => { _ = HandleAsync(s, message.ToString(), stoppingToken); }));
        while (!stoppingToken.IsCancellationRequested)
        {
            ISubscriber? subscriber = null;
            string? pending = null;
            try
            {
                var connection = await redis.GetAsync();
                subscriber = connection.GetSubscriber();
                foreach (var subscription in distinct.Where(s => !registered.Contains(s.Channel)))
                {
                    pending = subscription.Channel;
                    await subscriber.SubscribeAsync(
                        RedisChannel.Literal(subscription.Channel),
                        callbacks[subscription.Channel]);
                    registered.Add(subscription.Channel); pending = null;
                    logger.LogInformation("Subscribed to {Channel}", subscription.Channel);
                }
                return; // StackExchange.Redis maintains subscriptions on later reconnects.
            }
            catch (RedisException)
            {
                if (stoppingToken.IsCancellationRequested) return;
                if (pending is not null && subscriber is not null)
                {
                    try { subscriber.Unsubscribe(RedisChannel.Literal(pending), callbacks[pending], CommandFlags.FireAndForget); }
                    catch (RedisException) { /* Local reconnect retry will try the channel again. */ }
                }
                // Legacy transport is degraded; durable HTTP admission must remain available.
                logger.LogWarning("Redis subscription unavailable; retrying without stopping the HTTP host.");
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            try { await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
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
