using Ago.Platform.Abstractions;
using Ago.Platform.Caching.Redis;
using Microsoft.Extensions.Logging.Abstractions;
using Polly;

namespace Ago.Platform.Integration.Tests;

/// <summary>`3-05`: the token bucket, real Redis (Testcontainers, no mocking - testing.md). Every
/// test builds its own <see cref="RedisRateLimiter"/> against the shared container so tests using
/// different keys never race each other.</summary>
[Collection(RedisCollection.Name)]
public sealed class RedisRateLimiterTests(RedisFixture fixture)
{
    [Fact]
    public async Task CheckAsync_WithinCapacity_Allows()
    {
        var limiter = CreateLimiter();
        var key = new RateLimitKey($"test:{Guid.NewGuid():N}");
        var rule = new RateLimitRule(Capacity: 5, RefillPerSecond: 1);

        var decision = await limiter.CheckAsync(key, rule, CancellationToken.None);

        Assert.True(decision.Allowed);
    }

    [Fact]
    public async Task CheckAsync_OnceCapacityIsExhausted_DeniesWithARetryAfter()
    {
        var limiter = CreateLimiter();
        var key = new RateLimitKey($"test:{Guid.NewGuid():N}");
        var rule = new RateLimitRule(Capacity: 3, RefillPerSecond: 1);

        for (var i = 0; i < 3; i++)
        {
            var decision = await limiter.CheckAsync(key, rule, CancellationToken.None);
            Assert.True(decision.Allowed, $"Request {i} should still be within capacity.");
        }

        var denied = await limiter.CheckAsync(key, rule, CancellationToken.None);

        Assert.False(denied.Allowed);
        Assert.True(denied.RetryAfter > TimeSpan.Zero);
    }

    [Fact]
    public async Task CheckAsync_AfterTheRetryAfterElapses_AllowsAgain()
    {
        var limiter = CreateLimiter();
        var key = new RateLimitKey($"test:{Guid.NewGuid():N}");
        // A fast-refilling bucket so the test does not need to sleep long: capacity 1, 5 tokens/sec.
        var rule = new RateLimitRule(Capacity: 1, RefillPerSecond: 5);

        var first = await limiter.CheckAsync(key, rule, CancellationToken.None);
        Assert.True(first.Allowed);
        var denied = await limiter.CheckAsync(key, rule, CancellationToken.None);
        Assert.False(denied.Allowed);

        await Task.Delay(denied.RetryAfter + TimeSpan.FromMilliseconds(50));
        var afterWaiting = await limiter.CheckAsync(key, rule, CancellationToken.None);

        Assert.True(afterWaiting.Allowed);
    }

    [Fact]
    public async Task CheckAsync_ManyConcurrentRequests_AllowsExactlyCapacityAndDeniesTheRest()
    {
        var limiter = CreateLimiter();
        var key = new RateLimitKey($"test:{Guid.NewGuid():N}");
        // A refill rate slow enough that no meaningful refill happens during the burst below.
        var rule = new RateLimitRule(Capacity: 10, RefillPerSecond: 0.001);

        var decisions = await Task.WhenAll(Enumerable.Range(0, 50)
            .Select(_ => limiter.CheckAsync(key, rule, CancellationToken.None)));

        Assert.Equal(10, decisions.Count(d => d.Allowed));
        Assert.Equal(40, decisions.Count(d => !d.Allowed));
    }

    private RedisRateLimiter CreateLimiter() => new(
        fixture.Multiplexer, new ResiliencePipelineBuilder().AddTimeout(TimeSpan.FromSeconds(2)).Build(), NullLogger<RedisRateLimiter>.Instance);
}
