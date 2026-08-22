using System.Text.Json;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;

namespace Ago.Platform.Caching.Redis;

/// <summary>
/// Publishes <see cref="CacheInvalidated"/> for one key. Calls <see cref="IEventPublisher"/> directly,
/// not through the outbox - the same shape and justification as
/// <c>Ago.Platform.Realtime.NodeFanoutPublisher</c> (`adr/0020`, `IEventPublisher`'s own doc comment):
/// a cache-invalidation notice describes no committed state change of its own, it is derived from
/// whatever product event already went through the outbox for the write that actually changed
/// (`Ago.Chat.Worker`'s consumer of `SiteSettingsChanged`, for the one call site 3-04 ships). Losing
/// this publish is bounded by the cached entry's own TTL, not silent forever - the same accepted
/// failure mode `adr/0020` already documents for a lost delivery notification.
/// </summary>
public sealed class CacheInvalidationPublisher(IEventPublisher publisher, IClock clock)
{
    public Task PublishAsync(CacheKey key, Guid correlationId, CancellationToken cancellationToken)
    {
        var envelope = new EventEnvelope(
            MessageId: Guid.NewGuid(),
            Type: CacheTopics.Invalidated,
            Version: 1,
            PartitionKey: key.Value,
            OccurredAt: clock.UtcNow,
            CorrelationId: correlationId,
            Payload: JsonSerializer.Serialize(new CacheInvalidated(key.Value)));

        return publisher.PublishAsync(envelope, cancellationToken);
    }
}
