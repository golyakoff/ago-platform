using Ago.Platform.Abstractions;
using Ago.Platform.Caching.Redis;
using Microsoft.Extensions.Logging.Abstractions;
using Polly;
using StackExchange.Redis;
using Testcontainers.Redis;

namespace Ago.Platform.Integration.Tests;

/// <summary>`adr/0009`: "rate limits fail open to the next window" - a Redis outage must never turn
/// into rejected real traffic, only lost protection. Its own, non-shared container so stopping it
/// cannot affect any other test.</summary>
public sealed class RedisRateLimiterContainerFailureTests
{
    [Fact]
    public async Task ChecksAgainstAStoppedRedis_FailOpen_RatherThanThrowingOrDenying()
    {
        var container = new RedisBuilder("redis:7-alpine").Build();
        await container.StartAsync();

        var configuration = ConfigurationOptions.Parse(container.GetConnectionString());
        configuration.ConnectTimeout = 2000;
        configuration.SyncTimeout = 2000;
        configuration.AbortOnConnectFail = false;

        await using var multiplexer = await ConnectionMultiplexer.ConnectAsync(configuration);
        var resilience = new ResiliencePipelineBuilder().AddTimeout(TimeSpan.FromMilliseconds(500)).Build();
        var limiter = new RedisRateLimiter(multiplexer, resilience, NullLogger<RedisRateLimiter>.Instance);
        var key = new RateLimitKey($"test:{Guid.NewGuid():N}");
        var rule = new RateLimitRule(Capacity: 1, RefillPerSecond: 1);

        // Warm up (and exhaust the bucket) while Redis is still up - matching the cache/registry
        // container-failure tests' own reasoning.
        await limiter.CheckAsync(key, rule, CancellationToken.None);

        await container.StopAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        RateLimitDecision decision = default;
        var exception = await Record.ExceptionAsync(async () =>
        {
            decision = await limiter.CheckAsync(key, rule, cts.Token);
        });

        Assert.Null(exception);
        Assert.False(cts.IsCancellationRequested, "Should have returned on its own, not because the test's timeout fired.");
        Assert.True(decision.Allowed); // fails open, even though this exact bucket was already exhausted

        await container.DisposeAsync();
    }
}
