namespace Ago.Platform.Abstractions;

/// <summary>
/// Subscribes a handler to a topic. The handler decides the outcome explicitly through the supplied
/// <see cref="IMessageContext"/> - there is no implicit ack on return, matching
/// docs/architecture/messaging.md's "explicit ack" rule. Exchanges, bindings, consumer groups,
/// offsets and partition counts are adapter configuration, never exposed here (adr/0006).
/// </summary>
public interface IEventConsumer
{
    Task SubscribeAsync(
        string topic,
        SubscriptionMode mode,
        RetryPolicy retryPolicy,
        Func<EventEnvelope, IMessageContext, CancellationToken, Task> handler,
        CancellationToken cancellationToken);
}
