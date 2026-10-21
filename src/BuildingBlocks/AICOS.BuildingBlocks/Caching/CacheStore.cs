using System.Collections.Concurrent;
using AICOS.BuildingBlocks.Messaging;
using StackExchange.Redis;

namespace AICOS.BuildingBlocks.Caching;

/// <summary>
/// A small string cache for data that can always be rebuilt (NFR-25: menu and availability changes reach QR, POS and
/// takeaway quickly). A cache failure is never an error for the caller: it behaves like a miss.
/// </summary>
public interface ICacheStore
{
    Task<string?> GetAsync(string key, CancellationToken cancellationToken = default);

    Task SetAsync(string key, string value, TimeSpan timeToLive, CancellationToken cancellationToken = default);

    /// <summary>Adds one to a counter and returns the new value. Used as a "version": bumping it makes every key built from it obsolete.</summary>
    Task<long> IncrementAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Current value of a counter, 0 when it was never incremented.</summary>
    Task<long> GetCounterAsync(string key, CancellationToken cancellationToken = default);
}

internal sealed class RedisCacheStore(RedisConnectionProvider redis, ILogger<RedisCacheStore> logger) : ICacheStore
{
    public async Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        try
        {
            var value = await (await redis.GetAsync()).GetDatabase().StringGetAsync(key);
            return value.HasValue ? value.ToString() : null;
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            logger.LogWarning(ex, "Cache read failed for {Key}; treating it as a miss", key);
            return null;
        }
    }

    public async Task SetAsync(string key, string value, TimeSpan timeToLive, CancellationToken cancellationToken = default)
    {
        try
        {
            await (await redis.GetAsync()).GetDatabase().StringSetAsync(key, value, timeToLive);
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            logger.LogWarning(ex, "Cache write failed for {Key}", key);
        }
    }

    public async Task<long> IncrementAsync(string key, CancellationToken cancellationToken = default)
    {
        try
        {
            return await (await redis.GetAsync()).GetDatabase().StringIncrementAsync(key);
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            logger.LogWarning(ex, "Cache invalidation failed for {Key}", key);
            return 0;
        }
    }

    public async Task<long> GetCounterAsync(string key, CancellationToken cancellationToken = default)
    {
        try
        {
            var value = await (await redis.GetAsync()).GetDatabase().StringGetAsync(key);
            return value.HasValue && long.TryParse(value.ToString(), out var n) ? n : 0;
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            return 0;
        }
    }
}

/// <summary>Process-local cache. Used by tests, and a valid fallback for a single-instance demo without Redis.</summary>
public sealed class InMemoryCacheStore : ICacheStore
{
    private readonly ConcurrentDictionary<string, (string Value, DateTime ExpiresAtUtc)> _values = new();
    private readonly ConcurrentDictionary<string, long> _counters = new();

    public int Reads { get; private set; }

    /// <summary>Every key in use (values and counters), for tests that check how keys are named.</summary>
    public IReadOnlyCollection<string> Keys => [.. _values.Keys, .. _counters.Keys];

    public int Hits { get; private set; }

    public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        Reads++;
        if (_values.TryGetValue(key, out var entry) && entry.ExpiresAtUtc > DateTime.UtcNow)
        {
            Hits++;
            return Task.FromResult<string?>(entry.Value);
        }

        return Task.FromResult<string?>(null);
    }

    public Task SetAsync(string key, string value, TimeSpan timeToLive, CancellationToken cancellationToken = default)
    {
        _values[key] = (value, DateTime.UtcNow.Add(timeToLive));
        return Task.CompletedTask;
    }

    public Task<long> IncrementAsync(string key, CancellationToken cancellationToken = default) =>
        Task.FromResult(_counters.AddOrUpdate(key, 1, (_, current) => current + 1));

    public Task<long> GetCounterAsync(string key, CancellationToken cancellationToken = default) =>
        Task.FromResult(_counters.GetValueOrDefault(key));
}

public static class CacheExtensions
{
    /// <summary>Registers the Redis-backed <see cref="ICacheStore"/> (same connection and settings as the event bus).</summary>
    public static IServiceCollection AddAicosCache(this IServiceCollection services)
    {
        services.AddSingleton<RedisConnectionProvider>();
        services.AddSingleton<ICacheStore, RedisCacheStore>();
        return services;
    }
}
