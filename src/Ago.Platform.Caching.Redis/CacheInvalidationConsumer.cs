using System.Text.Json;
using Ago.Platform.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Ago.Platform.Caching.Redis;

/// <summary>
/// One per node that holds a cache worth invalidating - a host wires this up itself
/// (<c>AddHostedService&lt;CacheInvalidationConsumer&gt;</c>), the same way `Ago.Chat.Api` wires up
/// <c>NodeDeliveryConsumer</c>; `Ago.Platform.Caching.Redis.ServiceCollectionExtensions` registers the
/// DI surface but never the hosted service itself, since not every host that references this project
/// necessarily reads from the cache.
///
/// <see cref="SubscriptionMode.Broadcast"/>, not <see cref="SubscriptionMode.Competing"/> - the one
/// named exception in `messaging.md`'s Topics table: every node must drop the key, not just whichever
/// one happens to receive the message. Entirely generic: the payload is just a key,
/// <see cref="ICache.RemoveAsync"/> does not need to know why - no product knowledge lives here, which
/// is what keeps this in `Ago.Platform.*` (`clean-architecture.md`'s qualifying rule) rather than
/// `Ago.Chat.*`.
/// </summary>
public sealed class CacheInvalidationConsumer(
    IEventConsumer consumer, ICache cache, ILogger<CacheInvalidationConsumer> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var retryPolicy = new RetryPolicy(MaxAttempts: 1, InitialBackoff: TimeSpan.Zero, DeadLetterName: $"{CacheTopics.Invalidated}.dlq");
        return consumer.SubscribeAsync(CacheTopics.Invalidated, SubscriptionMode.Broadcast, retryPolicy, HandleAsync, stoppingToken);
    }

    private async Task HandleAsync(EventEnvelope envelope, IMessageContext context, CancellationToken cancellationToken)
    {
        try
        {
            var invalidated = JsonSerializer.Deserialize<CacheInvalidated>(envelope.Payload)
                ?? throw new InvalidOperationException($"Could not deserialize {nameof(CacheInvalidated)} for {envelope.MessageId}.");

            await cache.RemoveAsync(new CacheKey(invalidated.Key), cancellationToken);
            await context.AckAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Best-effort, ephemeral, same reasoning as NodeDeliveryConsumer: a stale cache entry
            // survives at most until its own TTL, never worth a requeue loop over.
            logger.LogWarning(ex, "Failed to process cache invalidation {MessageId}.", envelope.MessageId);
            await context.AckAsync(cancellationToken);
        }
    }
}
