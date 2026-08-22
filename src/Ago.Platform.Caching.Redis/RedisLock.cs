using Polly;
using StackExchange.Redis;

namespace Ago.Platform.Caching.Redis;

/// <summary>
/// `caching.md`'s "short Redis lock for the cross-node case" - a real distributed lock (`SET NX`
/// acquire, token-checked Lua-script release), not just a hope: releasing with a plain `DEL` would let
/// this process delete a *different* holder's lock after its own token's TTL had already expired and
/// someone else acquired it. Held for the shortest span consistent with cache-population latency, not
/// tuned or load-tested - the TTL is the correctness backstop if release itself never runs (a crash
/// between acquire and release), matching `RedisConnectionRegistry`'s own TTL-is-the-backstop shape.
/// </summary>
internal static class RedisLock
{
    private static readonly TimeSpan LockTtl = TimeSpan.FromSeconds(5);

    internal const string ReleaseScript = """
        if redis.call('get', KEYS[1]) == ARGV[1] then
            return redis.call('del', KEYS[1])
        else
            return 0
        end
        """;

    public static async Task<RedisLockHandle> TryAcquireAsync(
        IDatabase database, string key, ResiliencePipeline resilience, CancellationToken cancellationToken)
    {
        var token = Guid.NewGuid().ToString("N");
        try
        {
            // .WaitAsync(ct): StackExchange.Redis's async API takes no CancellationToken of its own -
            // see RedisCache's own remarks on why this is required for Polly's timeout to mean
            // anything against an unreachable Redis.
            var acquired = await resilience.ExecuteAsync(
                async ct => await database.StringSetAsync(key, token, LockTtl, When.NotExists).WaitAsync(ct),
                cancellationToken);
            return new RedisLockHandle(acquired ? database : null, key, token, owned: acquired, reachable: true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Redis unreachable for the acquire itself - proceed unlocked (RedisCache's caller falls
            // back to loading directly, and skips its own poll-and-wait loop entirely: polling a
            // Redis that just failed to even grant a lock is a wasted wait, not a meaningful check).
            return new RedisLockHandle(null, key, token, owned: false, reachable: false);
        }
    }
}

internal sealed class RedisLockHandle(IDatabase? database, string key, string token, bool owned, bool reachable) : IAsyncDisposable
{
    public bool Owned { get; } = owned;

    /// <summary>False only when the acquire attempt itself failed against Redis - as opposed to
    /// Owned being false because some other holder legitimately has the lock right now.</summary>
    public bool Reachable { get; } = reachable;

    public async ValueTask DisposeAsync()
    {
        if (!Owned || database is null)
        {
            return;
        }

        try
        {
            // A fixed, short timeout rather than an external CancellationToken - IAsyncDisposable
            // has none to give, and release must never hang: the lock's own TTL is the correctness
            // backstop if this never completes (a crash, Redis going down between acquire and
            // release), documented on the type above.
            await database.ScriptEvaluateAsync(RedisLock.ReleaseScript, [(RedisKey)key], [(RedisValue)token])
                .WaitAsync(TimeSpan.FromMilliseconds(500));
        }
        catch
        {
            // Best-effort release - see the type's own remarks above.
        }
    }
}
