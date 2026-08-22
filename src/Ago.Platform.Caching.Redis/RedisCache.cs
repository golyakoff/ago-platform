using System.Collections.Concurrent;
using System.Text.Json;
using Ago.Platform.Abstractions;
using Microsoft.Extensions.Logging;
using Polly;
using StackExchange.Redis;

namespace Ago.Platform.Caching.Redis;

/// <summary>
/// `caching.md`'s port, implemented against Redis. Every Redis-touching call runs through a shared
/// <see cref="ResiliencePipeline"/> (short timeout, circuit breaker) and degrades to a cache miss on
/// failure rather than throwing (`adr/0009`, `resilience.md`'s Redis row) - a caller's own data-source
/// call inside <see cref="GetOrCreateAsync{T}"/>'s <c>factory</c> is a different matter and is never
/// swallowed here; a real failure to load the underlying data is not a cache concern.
/// </summary>
public sealed class RedisCache(IConnectionMultiplexer multiplexer, ResiliencePipeline resilience, ILogger<RedisCache> logger) : ICache
{
    // Per-process single-flight for GetOrCreateAsync's stampede protection (caching.md's Patterns
    // section): every concurrent caller for the same cold key awaits the same Lazy<Task>, so the
    // factory runs once per key per process. Keyed by the raw string, not CacheKey, only because
    // ConcurrentDictionary needs a plain equatable key and CacheKey already wraps one.
    private readonly ConcurrentDictionary<string, Lazy<Task<object?>>> _inFlight = new();

    private IDatabase Database => multiplexer.GetDatabase();

    public async Task<T?> GetAsync<T>(CacheKey key, CancellationToken cancellationToken)
    {
        try
        {
            // .WaitAsync(ct), on every Redis call in this type: StackExchange.Redis's async API takes
            // no CancellationToken of its own, so without this, Polly's timeout strategy has nothing
            // to actually cancel and a call against an unreachable Redis can run far past the
            // configured timeout - found by running RedisCacheContainerFailureTests for real and
            // watching it take 20s instead of a fraction of a second (RedisConnectionRegistry, 3-01,
            // already does the same .WaitAsync(cancellationToken) for exactly this reason).
            var value = await resilience.ExecuteAsync(
                async ct => await Database.StringGetAsync(key.Value).WaitAsync(ct), cancellationToken);
            return value.IsNullOrEmpty ? default : JsonSerializer.Deserialize<T>((string)value!);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Cache read failed for {Key} - treating it as a miss.", key);
            return default;
        }
    }

    public async Task SetAsync<T>(CacheKey key, T value, CacheEntryOptions options, CancellationToken cancellationToken)
    {
        try
        {
            var json = JsonSerializer.Serialize(value);
            await resilience.ExecuteAsync(
                async ct => await Database.StringSetAsync(key.Value, json, Jitter(options.Ttl)).WaitAsync(ct), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Cache write failed for {Key} - continuing without caching it.", key);
        }
    }

    public async Task<T> GetOrCreateAsync<T>(
        CacheKey key, Func<CancellationToken, Task<T>> factory, CacheEntryOptions options, CancellationToken cancellationToken)
    {
        if (await GetAsync<T>(key, cancellationToken) is { } hit)
        {
            return hit;
        }

        var lazy = _inFlight.GetOrAdd(key.Value, _ => new Lazy<Task<object?>>(PopulateAsync));

        try
        {
            return (T)(await lazy.Value)!;
        }
        finally
        {
            // Remove only if this call's own Lazy is still the one on record - a fresh miss started
            // after this one finished may already have installed a newer one.
            ((ICollection<KeyValuePair<string, Lazy<Task<object?>>>>)_inFlight).Remove(
                new KeyValuePair<string, Lazy<Task<object?>>>(key.Value, lazy));
        }

        async Task<object?> PopulateAsync()
        {
            // Someone else may have populated the key while this call waited to become the
            // single-flight owner - another in-flight load for the same key that finished just before
            // this one attached, or a write that arrived from another node.
            if (await GetAsync<T>(key, cancellationToken) is { } doubleChecked)
            {
                return doubleChecked;
            }

            await using var acquired = await RedisLock.TryAcquireAsync(Database, LockKey(key), resilience, cancellationToken);
            if (!acquired.Owned && acquired.Reachable)
            {
                // Another node is (probably) already loading this key - give it a short, bounded
                // window to finish rather than duplicating its work, then fall back to loading
                // ourselves. Never wait indefinitely on a lock that might not release cleanly (a
                // crashed holder, a network partition) - the point is reducing duplicate cross-node
                // loads, not guaranteeing exactly one the way the in-process single-flight above does.
                // Skipped entirely when Redis itself was unreachable (acquired.Reachable is false) -
                // polling a Redis that just failed to grant the lock is a wasted wait, not a real check.
                for (var attempt = 0; attempt < 5; attempt++)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
                    if (await GetAsync<T>(key, cancellationToken) is { } value)
                    {
                        return value;
                    }
                }
            }

            var loaded = await factory(cancellationToken);

            // The factory may have already written this key itself, with its own CacheEntryOptions
            // (e.g. a shorter negative-cache TTL, ICache.GetOrCreateAsync's own doc comment) -
            // respecting that write instead of clobbering it with `options` is what lets a caller vary
            // TTL by outcome without a second parameter on this method.
            if (await GetAsync<T>(key, cancellationToken) is null)
            {
                await SetAsync(key, loaded, options, cancellationToken);
            }

            return loaded;
        }
    }

    public async Task RemoveAsync(CacheKey key, CancellationToken cancellationToken)
    {
        try
        {
            await resilience.ExecuteAsync(async ct => await Database.KeyDeleteAsync(key.Value).WaitAsync(ct), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Cache remove failed for {Key} - it may still be present until its TTL expires.", key);
        }
    }

    private static string LockKey(CacheKey key) => $"lock:{key.Value}";

    // +/- 10% (caching.md) - entries created together must not expire together and synchronise a
    // thundering herd. Applied once, here, never by a call site.
    private static TimeSpan Jitter(TimeSpan ttl)
    {
        var factor = 0.9 + (Random.Shared.NextDouble() * 0.2);
        return TimeSpan.FromTicks((long)(ttl.Ticks * factor));
    }
}
