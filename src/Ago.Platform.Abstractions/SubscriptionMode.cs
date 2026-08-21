namespace Ago.Platform.Abstractions;

/// <summary>
/// docs/architecture/messaging.md: <see cref="Competing"/> is the default - one delivery per
/// logical message, load-shared across consumer instances. <see cref="Broadcast"/> is the one named
/// exception (<c>CacheInvalidated</c>) where every node must receive every message; the adapter hides
/// how each is achieved per broker (a shared queue vs. a per-node exclusive queue on a fanout
/// exchange for RabbitMQ, a shared vs. per-node-unique consumer group for Kafka).
/// </summary>
public enum SubscriptionMode
{
    Competing,
    Broadcast,
}
