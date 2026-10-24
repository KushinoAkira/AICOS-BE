using System.Collections.Concurrent;
using AICOS.BuildingBlocks.Exceptions;
using AICOS.BuildingBlocks.Messaging;
using StackExchange.Redis;

namespace AICOS.BuildingBlocks.Caching;

/// <summary>
/// Short-lived records made of independent fields (a QR table cart: one field per line), so two phones changing different lines at the same
/// moment never overwrite each other. Unlike <see cref="ICacheStore"/> this is not a cache: the data exists only here, so a Redis failure is
/// reported to the caller as <see cref="DependencyUnavailableException"/> instead of being treated as a miss.
/// </summary>
public interface IHashStore
{
    Task SetFieldAsync(string key, string field, string value, TimeSpan timeToLive, CancellationToken cancellationToken = default);

    /// <summary>Adds <paramref name="delta"/> to a numeric field (created at 0) in one atomic step and returns the new value.</summary>
    Task<long> IncrementFieldAsync(string key, string field, long delta, TimeSpan timeToLive, CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<string, string>> GetAllAsync(string key, CancellationToken cancellationToken = default);

    Task DeleteFieldsAsync(string key, IReadOnlyCollection<string> fields, CancellationToken cancellationToken = default);

    Task DeleteAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Reads every field and deletes the record in one atomic step: of two callers racing for the same record only one gets the fields.</summary>
    Task<IReadOnlyDictionary<string, string>> TakeAllAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Pushes the expiry of the whole record forward.</summary>
    Task TouchAsync(string key, TimeSpan timeToLive, CancellationToken cancellationToken = default);
}

internal sealed class RedisHashStore(RedisConnectionProvider redis) : IHashStore
{
    private async Task<T> RunAsync<T>(Func<IDatabase, Task<T>> action)
    {
        try
        {
            return await action((await redis.GetAsync()).GetDatabase());
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            throw new DependencyUnavailableException("redis");
        }
    }

    public Task SetFieldAsync(string key, string field, string value, TimeSpan timeToLive, CancellationToken cancellationToken = default) =>
        RunAsync(async db =>
        {
            await db.HashSetAsync(key, field, value);
            await db.KeyExpireAsync(key, timeToLive);
            return true;
        });

    public Task<long> IncrementFieldAsync(string key, string field, long delta, TimeSpan timeToLive, CancellationToken cancellationToken = default) =>
        RunAsync(async db =>
        {
            var value = await db.HashIncrementAsync(key, field, delta);
            await db.KeyExpireAsync(key, timeToLive);
            return value;
        });

    public Task<IReadOnlyDictionary<string, string>> GetAllAsync(string key, CancellationToken cancellationToken = default) =>
        RunAsync(async db =>
        {
            var entries = await db.HashGetAllAsync(key);
            return (IReadOnlyDictionary<string, string>)entries.ToDictionary(e => e.Name.ToString(), e => e.Value.ToString());
        });

    public Task DeleteFieldsAsync(string key, IReadOnlyCollection<string> fields, CancellationToken cancellationToken = default) =>
        RunAsync(async db =>
        {
            await db.HashDeleteAsync(key, fields.Select(f => (RedisValue)f).ToArray());
            return true;
        });

    public Task DeleteAsync(string key, CancellationToken cancellationToken = default) =>
        RunAsync(async db => await db.KeyDeleteAsync(key));

    public Task<IReadOnlyDictionary<string, string>> TakeAllAsync(string key, CancellationToken cancellationToken = default) =>
        RunAsync(async db =>
        {
            var tx = db.CreateTransaction();
            var read = tx.HashGetAllAsync(key);
            _ = tx.KeyDeleteAsync(key);
            await tx.ExecuteAsync();
            return (IReadOnlyDictionary<string, string>)(await read).ToDictionary(e => e.Name.ToString(), e => e.Value.ToString());
        });

    public Task TouchAsync(string key, TimeSpan timeToLive, CancellationToken cancellationToken = default) =>
        RunAsync(async db => await db.KeyExpireAsync(key, timeToLive));
}

/// <summary>Process-local hash store for tests and a single-instance demo without Redis. Expiry is not simulated.</summary>
public sealed class InMemoryHashStore : IHashStore
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, string>> _records = new();
    private readonly object _gate = new();

    /// <summary>Set to simulate a Redis outage.</summary>
    public bool Down { get; set; }

    public IReadOnlyCollection<string> Keys => _records.Keys.ToList();

    private ConcurrentDictionary<string, string> Record(string key)
    {
        if (Down)
        {
            throw new DependencyUnavailableException("redis");
        }

        return _records.GetOrAdd(key, _ => new ConcurrentDictionary<string, string>());
    }

    public Task SetFieldAsync(string key, string field, string value, TimeSpan timeToLive, CancellationToken cancellationToken = default)
    {
        Record(key)[field] = value;
        return Task.CompletedTask;
    }

    public Task<long> IncrementFieldAsync(string key, string field, long delta, TimeSpan timeToLive, CancellationToken cancellationToken = default)
    {
        var record = Record(key);
        lock (_gate)
        {
            var next = (record.TryGetValue(field, out var current) ? long.Parse(current) : 0) + delta;
            record[field] = next.ToString();
            return Task.FromResult(next);
        }
    }

    public Task<IReadOnlyDictionary<string, string>> GetAllAsync(string key, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>(Record(key)));

    public Task DeleteFieldsAsync(string key, IReadOnlyCollection<string> fields, CancellationToken cancellationToken = default)
    {
        var record = Record(key);
        foreach (var field in fields)
        {
            record.TryRemove(field, out _);
        }

        return Task.CompletedTask;
    }

    public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        Record(key);
        _records.TryRemove(key, out _);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyDictionary<string, string>> TakeAllAsync(string key, CancellationToken cancellationToken = default)
    {
        Record(key);
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyDictionary<string, string>>(
                _records.TryRemove(key, out var record) ? new Dictionary<string, string>(record) : new Dictionary<string, string>());
        }
    }

    public Task TouchAsync(string key, TimeSpan timeToLive, CancellationToken cancellationToken = default)
    {
        Record(key);
        return Task.CompletedTask;
    }
}
