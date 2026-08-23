namespace Ago.Platform.Abstractions;

/// <summary>
/// Subscribes a handler to a topic. The handler decides the outcome explicitly through the supplied
/// <see cref="IMessageContext"/> - there is no implicit ack on return, matching
/// docs/architecture/messaging.md's "explicit ack" rule. Exchanges, bindings, offsets and partition
/// counts stay adapter configuration, never exposed here (adr/0006) - <paramref name="consumerName"/>
/// is not that: it is not Kafka's own consumer-group *mechanism* leaking through, it is the caller
/// declaring *identity* ("which logical consumer am I"), which <see cref="SubscriptionMode.Competing"/>
/// cannot mean anything without on either broker. Two independent consumer types that both need
/// every message (`UnreadCounterConsumer` and `ConnectionFanoutConsumer` both reacting to
/// `MessageAccepted`, found live in `5-11`) must pass different names; N replicas of the *same*
/// logical consumer (true horizontal scaling, `Competing`'s actual purpose) pass the *same* name.
/// Required for both modes for one signature rather than a conditionally-required parameter -
/// <see cref="SubscriptionMode.Broadcast"/> does not need it for correctness (its own subscription is
/// already unique), but every subscriber still has a name worth logging.
/// </summary>
public interface IEventConsumer
{
    Task SubscribeAsync(
        string topic,
        SubscriptionMode mode,
        string consumerName,
        RetryPolicy retryPolicy,
        Func<EventEnvelope, IMessageContext, CancellationToken, Task> handler,
        CancellationToken cancellationToken);
}
