using Ago.Platform.Caching.Redis;
using Microsoft.Extensions.Logging.Abstractions;
using Polly;

namespace Ago.Platform.Integration.Tests;

/// <summary>`4-03`'s exclusivity claim: only one caller ever holds a given key at a time, and
/// releasing genuinely frees it for the next one - a real distributed lock, not just SQL-shaped
/// intent.</summary>
[Collection(RedisCollection.Name)]
public sealed class RedisDistributedLockTests(RedisFixture fixture)
{
    private RedisDistributedLock CreateLock() => new(
        fixture.Multiplexer, new ResiliencePipelineBuilder().AddTimeout(TimeSpan.FromSeconds(2)).Build(),
        NullLogger<RedisDistributedLock>.Instance);

    [Fact]
    public async Task TryAcquireAsync_WhenFree_Succeeds_AndBlocksAConcurrentSecondAcquire()
    {
        var key = $"test-lock:{Guid.NewGuid():N}";
        var redisLock = CreateLock();

        var first = await redisLock.TryAcquireAsync(key, TimeSpan.FromSeconds(10), CancellationToken.None);
        Assert.NotNull(first);

        var second = await redisLock.TryAcquireAsync(key, TimeSpan.FromSeconds(10), CancellationToken.None);
        Assert.Null(second);

        await first!.DisposeAsync();
    }

    [Fact]
    public async Task TryAcquireAsync_AfterTheHolderReleases_CanBeAcquiredAgain()
    {
        var key = $"test-lock:{Guid.NewGuid():N}";
        var redisLock = CreateLock();

        var first = await redisLock.TryAcquireAsync(key, TimeSpan.FromSeconds(10), CancellationToken.None);
        Assert.NotNull(first);
        await first!.DisposeAsync();

        var second = await redisLock.TryAcquireAsync(key, TimeSpan.FromSeconds(10), CancellationToken.None);
        Assert.NotNull(second);
        await second!.DisposeAsync();
    }

    [Fact]
    public async Task TryAcquireAsync_UnderConcurrentCallers_ExactlyOneSucceeds()
    {
        var key = $"test-lock:{Guid.NewGuid():N}";
        var redisLock = CreateLock();

        var results = await Task.WhenAll(Enumerable.Range(0, 10)
            .Select(_ => redisLock.TryAcquireAsync(key, TimeSpan.FromSeconds(10), CancellationToken.None)));

        var acquired = results.Where(handle => handle is not null).ToList();
        Assert.Single(acquired);

        foreach (var handle in acquired)
        {
            await handle!.DisposeAsync();
        }
    }
}
