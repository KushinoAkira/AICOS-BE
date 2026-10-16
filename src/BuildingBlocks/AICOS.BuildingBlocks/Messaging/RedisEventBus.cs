using System.Text.Json;
using AICOS.Contracts;
using StackExchange.Redis;

namespace AICOS.BuildingBlocks.Messaging;

/// <summary>Lazily-created, shared Redis connection. Never blocks service start-up if Redis is down.</summary>
internal sealed class RedisConnectionProvider(IConfiguration configuration) : IAsyncDisposable
{
    private readonly Lazy<Task<IConnectionMultiplexer>> _connection = new(() => ConnectAsync(configuration));

    public Task<IConnectionMultiplexer> GetAsync() => _connection.Value;

    private static async Task<IConnectionMultiplexer> ConnectAsync(IConfiguration configuration)
    {
        var options = ConfigurationOptions.Parse(configuration.GetConnectionString("Redis") ?? "localhost:6379");
        options.AbortOnConnectFail = false; // keep retrying in the background
        options.ConnectTimeout = 3000;
        return await ConnectionMultiplexer.ConnectAsync(options);
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection.IsValueCreated)
        {
            var connection = await _connection.Value;
            await connection.DisposeAsync();
        }
    }
}

internal sealed class RedisEventBus(RedisConnectionProvider redis, ILogger<RedisEventBus> logger) : IEventBus
{
    public async Task PublishAsync<TEvent>(TEvent @event, CancellationToken cancellationToken = default)
        where TEvent : IntegrationEvent
    {
        cancellationToken.ThrowIfCancellationRequested();

        var connection = await redis.GetAsync();
        var payload = JsonSerializer.Serialize(@event, EventBusConventions.Json);
        var channel = EventBusConventions.ChannelFor<TEvent>();

        var receivers = await connection.GetSubscriber().PublishAsync(RedisChannel.Literal(channel), payload);
        logger.LogDebug("Published {EventType} {EventId} to {Receivers} subscriber(s)", typeof(TEvent).Name, @event.EventId, receivers);
    }
}
