using Ago.Platform.Abstractions;
using Ago.Platform.Caching.Redis;
using Microsoft.Extensions.Logging.Abstractions;
using Polly;
using StackExchange.Redis;
using Testcontainers.Redis;

namespace Ago.Platform.Integration.Tests;

/// <summary>`adr/0009`'s "FLUSHALL is survivable" claim and `resilience.md`'s Redis row ("fallback to
/// cache miss - never surface an error"), exercised for real against a container stopped mid-test -
/// the same shape as <c>ConnectionRegistryContainerFailureTests</c>. Its own, non-shared container so
/// stopping it cannot affect any other test in the shared collection.</summary>
public sealed class RedisCacheContainerFailureTests
{
    [Fact]
    public async Task ReadsAgainstAStoppedRedis_DegradeToACacheMiss_RatherThanThrowing()
    {
        var container = new RedisBuilder("redis:7-alpine").Build();
        await container.StartAsync();

        var configuration = ConfigurationOptions.Parse(container.GetConnectionString());
        configuration.ConnectTimeout = 2000;
        configuration.SyncTimeout = 2000;
        configuration.AbortOnConnectFail = false;

        await using var multiplexer = await ConnectionMultiplexer.ConnectAsync(configuration);
        var resilience = new ResiliencePipelineBuilder().AddTimeout(TimeSpan.FromMilliseconds(500)).Build();
        var cache = new RedisCache(multiplexer, resilience, NullLogger<RedisCache>.Instance);
        var key = new CacheKey($"test:{Guid.NewGuid():N}");

        // Warm up while Redis is still up - matching the registry's own container-failure test, a
        // cache that only later finds Redis gone is the interesting case.
        await cache.SetAsync(key, "warm", new CacheEntryOptions(TimeSpan.FromMinutes(1)), CancellationToken.None);

        await container.StopAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var getException = await Record.ExceptionAsync(() => cache.GetAsync<string>(key, cts.Token));
        Assert.Null(getException);
        Assert.False(cts.IsCancellationRequested, "Should have returned on its own, not because the test's timeout fired.");

        var calls = 0;
        var value = await cache.GetOrCreateAsync(
            key,
            ct =>
            {
                Interlocked.Increment(ref calls);
                return Task.FromResult("loaded despite redis being down");
            },
            new CacheEntryOptions(TimeSpan.FromMinutes(1)),
            cts.Token);

        Assert.Equal("loaded despite redis being down", value);
        Assert.Equal(1, calls); // the factory still ran - a dead cache degrades speed, never correctness

        await container.DisposeAsync();
    }
}
