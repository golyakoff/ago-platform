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

    [Fact]
    public async Task GetOrCreateAsync_WhenTheCachedValueLooksLikeADefault_StillCallsTheFactoryOnlyOnce_NotEveryTime()
    {
        // A real bug, found live while building 5-01 (ago-chat): for an *unconstrained* generic T,
        // C#'s T? return annotation has no runtime effect when T is a value type - default(T?) for
        // T = bool is `false`, not a distinguishable null - so a factory whose real, legitimate
        // result happens to look like a "falsy" value could be mistaken for a cache miss and get
        // re-run on every call, defeating GetOrCreateAsync's whole contract. ICache now constrains
        // T : class specifically so this can never compile for a raw value type again; this test
        // proves the *reference-type* case - a DTO wrapping a value that is itself the "empty"/
        // default-looking one - still round-trips correctly and the factory still runs exactly once,
        // the actual guarantee this method promises.
        var cache = CreateCache();
        var key = new CacheKey($"test:{Guid.NewGuid():N}");
        var calls = 0;

        Task<FalsyResult> Factory(CancellationToken ct)
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(new FalsyResult(false));
        }

        var first = await cache.GetOrCreateAsync(key, Factory, new CacheEntryOptions(TimeSpan.FromMinutes(1)), CancellationToken.None);
        var second = await cache.GetOrCreateAsync(key, Factory, new CacheEntryOptions(TimeSpan.FromMinutes(1)), CancellationToken.None);

        Assert.False(first.Value);
        Assert.False(second.Value);
        Assert.Equal(1, calls);
    }

    private sealed record FalsyResult(bool Value);

    private RedisCache CreateCache() => new(fixture.Multiplexer, TestResiliencePipeline, NullLogger<RedisCache>.Instance);

    private static readonly ResiliencePipeline TestResiliencePipeline = new ResiliencePipelineBuilder()
        .AddTimeout(TimeSpan.FromSeconds(2))
        .Build();
}
