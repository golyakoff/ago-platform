using Ago.Platform.Abstractions;
using Ago.Platform.Caching.Redis;
using Microsoft.Extensions.Logging.Abstractions;
using Polly;
using StackExchange.Redis;

namespace Ago.Platform.Integration.Tests;

/// <summary>`3-04`: `caching.md`'s port implemented against real Redis (Testcontainers, no mocking -
/// testing.md). Every test builds its own <see cref="RedisCache"/> against the shared container so
/// tests using different keys never race each other.</summary>
[Collection(RedisCollection.Name)]
public sealed class RedisCacheTests(RedisFixture fixture)
{
    [Fact]
    public async Task SetAsync_ThenGetAsync_RoundTripsTheValue()
    {
        var cache = CreateCache();
        var key = new CacheKey($"test:{Guid.NewGuid():N}");

        await cache.SetAsync(key, "hello", new CacheEntryOptions(TimeSpan.FromMinutes(1)), CancellationToken.None);
        var value = await cache.GetAsync<string>(key, CancellationToken.None);

        Assert.Equal("hello", value);
    }

    [Fact]
    public async Task GetAsync_OnAMissingKey_ReturnsDefault()
    {
        var cache = CreateCache();

        var value = await cache.GetAsync<string>(new CacheKey($"test:{Guid.NewGuid():N}"), CancellationToken.None);

        Assert.Null(value);
    }

    [Fact]
    public async Task GetOrCreateAsync_OnAColdKey_CallsTheFactoryOnceAndCachesTheResult()
    {
        var cache = CreateCache();
        var key = new CacheKey($"test:{Guid.NewGuid():N}");
        var calls = 0;

        Task<string> Factory(CancellationToken ct)
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult("loaded");
        }

        var first = await cache.GetOrCreateAsync(key, Factory, new CacheEntryOptions(TimeSpan.FromMinutes(1)), CancellationToken.None);
        var second = await cache.GetOrCreateAsync(key, Factory, new CacheEntryOptions(TimeSpan.FromMinutes(1)), CancellationToken.None);

        Assert.Equal("loaded", first);
        Assert.Equal("loaded", second);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task GetOrCreateAsync_ManyConcurrentCallersAgainstAColdKey_CallTheFactoryExactlyOnce()
    {
        var cache = CreateCache();
        var key = new CacheKey($"test:{Guid.NewGuid():N}");
        var calls = 0;

        async Task<string> Factory(CancellationToken ct)
        {
            Interlocked.Increment(ref calls);
            await Task.Delay(200, ct); // wide enough that concurrent callers genuinely overlap
            return "loaded";
        }

        var results = await Task.WhenAll(Enumerable.Range(0, 20)
            .Select(_ => cache.GetOrCreateAsync(key, Factory, new CacheEntryOptions(TimeSpan.FromMinutes(1)), CancellationToken.None)));

        Assert.All(results, r => Assert.Equal("loaded", r));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task SetAsync_AppliesJitterWithinTenPercentOfTheNominalTtl()
    {
        var cache = CreateCache();
        var key = new CacheKey($"test:{Guid.NewGuid():N}");
        var nominal = TimeSpan.FromMinutes(10);

        await cache.SetAsync(key, "value", new CacheEntryOptions(nominal), CancellationToken.None);
        var ttl = await fixture.Multiplexer.GetDatabase().KeyTimeToLiveAsync(key.Value);

        Assert.NotNull(ttl);
        Assert.InRange(ttl.Value, nominal * 0.9 - TimeSpan.FromSeconds(2), nominal * 1.1);
    }

    [Fact]
    public async Task RemoveAsync_ThenGetAsync_NoLongerReturnsTheValue()
    {
        var cache = CreateCache();
        var key = new CacheKey($"test:{Guid.NewGuid():N}");
        await cache.SetAsync(key, "value", new CacheEntryOptions(TimeSpan.FromMinutes(1)), CancellationToken.None);

        await cache.RemoveAsync(key, CancellationToken.None);

        Assert.Null(await cache.GetAsync<string>(key, CancellationToken.None));
    }

    private RedisCache CreateCache() => new(fixture.Multiplexer, TestResiliencePipeline, NullLogger<RedisCache>.Instance);

    private static readonly ResiliencePipeline TestResiliencePipeline = new ResiliencePipelineBuilder()
        .AddTimeout(TimeSpan.FromSeconds(2))
        .Build();
}
