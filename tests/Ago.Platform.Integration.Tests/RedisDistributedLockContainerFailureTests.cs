using Ago.Platform.Caching.Redis;
using Microsoft.Extensions.Logging.Abstractions;
using Polly;
using StackExchange.Redis;
using Testcontainers.Redis;

namespace Ago.Platform.Integration.Tests;

/// <summary>
/// `4-03`'s fail-closed design claim, forced to happen for real rather than trusted from a comment:
/// unlike `RedisLock` (`3-04`, fails open), an unreachable Redis must make `TryAcquireAsync` return
/// `null` - the same outcome as "someone else holds it" - never throw, and never a false-positive
/// acquire. Its own, non-shared container so stopping it cannot affect any other test.
/// </summary>
public sealed class RedisDistributedLockContainerFailureTests
{
    [Fact]
    public async Task TryAcquireAsync_AgainstAStoppedRedis_ReturnsNull_RatherThanThrowingOrFalselyAcquiring()
    {
        var container = new RedisBuilder("redis:7-alpine").Build();
        await container.StartAsync();

        var configuration = ConfigurationOptions.Parse(container.GetConnectionString());
        configuration.ConnectTimeout = 2000;
        configuration.SyncTimeout = 2000;
        configuration.AbortOnConnectFail = false;

        await using var multiplexer = await ConnectionMultiplexer.ConnectAsync(configuration);
        var resilience = new ResiliencePipelineBuilder().AddTimeout(TimeSpan.FromMilliseconds(500)).Build();
        var redisLock = new RedisDistributedLock(multiplexer, resilience, NullLogger<RedisDistributedLock>.Instance);
        var key = $"test-lock:{Guid.NewGuid():N}";

        await container.StopAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        IAsyncDisposable? handle = null;
        var exception = await Record.ExceptionAsync(async () =>
        {
            handle = await redisLock.TryAcquireAsync(key, TimeSpan.FromSeconds(10), cts.Token);
        });

        Assert.Null(exception);
        Assert.False(cts.IsCancellationRequested, "Should have returned on its own, not because the test's timeout fired.");
        Assert.Null(handle); // fail-closed: unreachable Redis is treated as "not acquired," not as an error or a free pass

        await container.DisposeAsync();
    }
}
