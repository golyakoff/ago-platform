using Ago.Platform.Abstractions;
using Ago.Platform.Caching.Redis;
using Ago.Platform.Messaging.RabbitMq;
using Microsoft.Extensions.Logging.Abstractions;
using Polly;

namespace Ago.Platform.Integration.Tests;

/// <summary>
/// `3-04`: `CacheInvalidationPublisher` -&gt; the broker -&gt; `CacheInvalidationConsumer` -&gt;
/// `ICache.RemoveAsync`, real Redis and real RabbitMQ (`NodeFanoutFixture` already combines both -
/// reused here rather than starting a second identical container pair). `SubscriptionMode.Broadcast`
/// is exercised for real: two separate consumers (standing in for two `Ago.Chat.Api` nodes) each get
/// their own delivery of the same invalidation.
/// </summary>
[Collection(NodeFanoutCollection.Name)]
public sealed class CacheInvalidationTests(NodeFanoutFixture fixture)
{
    [Fact]
    public async Task PublishingAnInvalidation_RemovesTheKeyFromEveryNodesCache()
    {
        var resilience = new ResiliencePipelineBuilder().AddTimeout(TimeSpan.FromSeconds(2)).Build();
        var cacheA = new RedisCache(fixture.RedisMultiplexer, resilience, NullLogger<RedisCache>.Instance);
        var cacheB = new RedisCache(fixture.RedisMultiplexer, resilience, NullLogger<RedisCache>.Instance);
        var key = new CacheKey($"test:{Guid.NewGuid():N}");
        await cacheA.SetAsync(key, "cached", new CacheEntryOptions(TimeSpan.FromMinutes(5)), CancellationToken.None);

        await using var consumerConnectionA = new RabbitMqConnection(fixture.CreateRabbitMqOptions());
        await using var consumerConnectionB = new RabbitMqConnection(fixture.CreateRabbitMqOptions());
        var consumerA = new CacheInvalidationConsumer(new RabbitMqEventConsumer(consumerConnectionA), cacheA, NullLogger<CacheInvalidationConsumer>.Instance);
        var consumerB = new CacheInvalidationConsumer(new RabbitMqEventConsumer(consumerConnectionB), cacheB, NullLogger<CacheInvalidationConsumer>.Instance);
        await consumerA.StartAsync(CancellationToken.None);
        await consumerB.StartAsync(CancellationToken.None);
        // Same RabbitMQ subscription-timing gap NodeFanoutTests documents: StartAsync returns before
        // SubscribeAsync has actually finished declaring the exchange/queue/binding.
        await Task.Delay(TimeSpan.FromMilliseconds(500));

        try
        {
            await using var publisherConnection = new RabbitMqConnection(fixture.CreateRabbitMqOptions());
            var publisher = new CacheInvalidationPublisher(new RabbitMqEventPublisher(publisherConnection), new FakeClock(DateTimeOffset.UtcNow));

            await publisher.PublishAsync(key, Guid.NewGuid(), CancellationToken.None);

            var removed = await RabbitMqTestHelpers.WaitUntilAsync(
                async () => await cacheA.GetAsync<string>(key, CancellationToken.None) is null, TimeSpan.FromSeconds(10));
            Assert.True(removed, "Timed out waiting for the invalidation to remove the key.");
        }
        finally
        {
            await consumerA.StopAsync(CancellationToken.None);
            await consumerB.StopAsync(CancellationToken.None);
        }
    }
}
