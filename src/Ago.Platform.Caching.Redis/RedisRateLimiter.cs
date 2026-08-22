using Ago.Platform.Abstractions;
using Microsoft.Extensions.Logging;
using Polly;
using StackExchange.Redis;

namespace Ago.Platform.Caching.Redis;

/// <summary>
/// `caching.md`'s token bucket, atomic check-and-decrement in one round trip via a Lua script -
/// never a read-then-write from C#, which would let two concurrent requests both read "1 token left"
/// and both decrement, the exact race the Lua script exists to close. Time comes from Redis's own
/// `TIME` command, not the caller's clock: two app nodes racing the same bucket must agree on how
/// much time has passed, and their own clocks can disagree (`date-and-time.md`'s "never sort by a
/// clock you do not control" reasoning, applied to a shared counter instead of message ordering).
///
/// Same resilience shape as <see cref="RedisCache"/> (shared pipeline, `.WaitAsync(cancellationToken)`
/// on the call itself - see its remarks for why that second part is load-bearing), but a different
/// failure mode: a cache miss on failure re-reads the source of truth, so it is always correct, only
/// slower; a rate-limit check has no "source of truth" to fall back to, so failing open
/// (<c>Allowed: true</c>) is the only choice that does not either lie about the answer or reject real
/// traffic because Redis is down (`IRateLimiter`'s own doc comment, `adr/0009`).
/// </summary>
public sealed class RedisRateLimiter(
    IConnectionMultiplexer multiplexer, ResiliencePipeline resilience, ILogger<RedisRateLimiter> logger) : IRateLimiter
{
    // KEYS[1] = bucket key. ARGV[1] = capacity. ARGV[2] = refill tokens/second.
    // Returns {allowed (0/1), retry_after_seconds}. A bucket with no prior entry starts full (a
    // fresh visitor/site is not penalised for buckets it has never touched).
    private const string TokenBucketScript = """
        local tokens_field = 'tokens'
        local ts_field = 'ts'
        local capacity = tonumber(ARGV[1])
        local refill_per_second = tonumber(ARGV[2])

        local time = redis.call('TIME')
        local now = tonumber(time[1]) + (tonumber(time[2]) / 1000000)

        local bucket = redis.call('HMGET', KEYS[1], tokens_field, ts_field)
        local tokens = tonumber(bucket[1])
        local last = tonumber(bucket[2])
        if tokens == nil then
            tokens = capacity
            last = now
        end

        local elapsed = math.max(0, now - last)
        tokens = math.min(capacity, tokens + (elapsed * refill_per_second))

        local allowed = 0
        local retry_after = 0
        if tokens >= 1 then
            tokens = tokens - 1
            allowed = 1
        else
            retry_after = (1 - tokens) / refill_per_second
        end

        local ttl = math.ceil(capacity / refill_per_second) + 1
        redis.call('HSET', KEYS[1], tokens_field, tostring(tokens), ts_field, tostring(now))
        redis.call('EXPIRE', KEYS[1], ttl)

        return {allowed, tostring(retry_after)}
        """;

    public async Task<RateLimitDecision> CheckAsync(RateLimitKey key, RateLimitRule rule, CancellationToken cancellationToken)
    {
        try
        {
            var raw = await resilience.ExecuteAsync(
                async ct =>
                {
                    var database = multiplexer.GetDatabase();
                    return await database.ScriptEvaluateAsync(
                        TokenBucketScript,
                        [(RedisKey)key.Value],
                        [(RedisValue)rule.Capacity, (RedisValue)rule.RefillPerSecond])
                        .WaitAsync(ct);
                },
                cancellationToken);

            var result = (RedisResult[])raw!;
            var allowed = (long)result[0] == 1;
            var retryAfterSeconds = (double)result[1];
            return new RateLimitDecision(allowed, TimeSpan.FromSeconds(Math.Max(0, retryAfterSeconds)));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Rate limit check failed for {Key} - failing open.", key);
            return new RateLimitDecision(Allowed: true, RetryAfter: TimeSpan.Zero);
        }
    }
}
