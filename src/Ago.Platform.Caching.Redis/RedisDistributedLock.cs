using Microsoft.Extensions.Logging;
using Polly;
using StackExchange.Redis;

namespace Ago.Platform.Caching.Redis;

/// <summary>
/// A real distributed lock (`SET NX` acquire, token-checked Lua-script release - the same primitive
/// shape as `RedisLock`) with a deliberately different failure mode: **fail-closed**. `RedisLock`
/// (`3-04`'s cache-stampede protection) fails open on an unreachable Redis - correct there, since the
/// worst case is a redundant cache load. Here the worst case of failing open would be every caller
/// proceeding as if it held an exclusive lock it never actually got, defeating the entire point of
/// serializing access - so `TryAcquireAsync` returns `null` for *both* "someone else holds it" and
/// "could not even ask Redis," and callers must treat both identically: do not proceed.
///
/// Public, not internal like `RedisLock` - this is a product-agnostic primitive
/// (`naming-and-structure.md`'s "one project per external technology"), not folded into `ICache`'s
/// stampede-specific one.
/// </summary>
public sealed class RedisDistributedLock(IConnectionMultiplexer multiplexer, ResiliencePipeline resilience, ILogger<RedisDistributedLock> logger)
{
    private const string ReleaseScript = """
        if redis.call('get', KEYS[1]) == ARGV[1] then
            return redis.call('del', KEYS[1])
        else
            return 0
        end
        """;

    public async Task<IAsyncDisposable?> TryAcquireAsync(string key, TimeSpan ttl, CancellationToken cancellationToken)
    {
        var token = Guid.NewGuid().ToString("N");
        var database = multiplexer.GetDatabase();
        try
        {
            // .WaitAsync(ct): StackExchange.Redis's async API takes no CancellationToken of its own -
            // see RedisCache's own remarks on why this is required for Polly's timeout to mean
            // anything against an unreachable Redis.
            var acquired = await resilience.ExecuteAsync(
                async ct => await database.StringSetAsync(key, token, ttl, When.NotExists).WaitAsync(ct),
                cancellationToken);

            return acquired ? new Handle(database, key, token, logger) : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Fail-closed: Redis being unreachable for the acquire itself is indistinguishable from
            // "someone else holds it" to every caller - both mean "do not proceed."
            logger.LogDebug(ex, "Could not acquire distributed lock {Key} - Redis unreachable, treating as not acquired.", key);
            return null;
        }
    }

    private sealed class Handle(IDatabase database, string key, string token, ILogger logger) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try
            {
                // A fixed, short timeout rather than an external CancellationToken - IAsyncDisposable
                // has none to give, and release must never hang: the lock's own TTL is the
                // correctness backstop if this never completes (a crash, Redis going down between
                // acquire and release).
                await database.ScriptEvaluateAsync(ReleaseScript, [(RedisKey)key], [(RedisValue)token])
                    .WaitAsync(TimeSpan.FromMilliseconds(500));
            }
            catch (Exception ex)
            {
                // Best-effort release - the TTL is what actually bounds how long a failed release can
                // block the next acquirer, not this call succeeding.
                logger.LogDebug(ex, "Failed to release distributed lock {Key} - its TTL is the backstop.", key);
            }
        }
    }
}
