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
    /// <summary>
    /// Durable-queue overload, unchanged since `5-11`. Every existing caller keeps compiling and
    /// keeps today's behaviour without edit - a `Competing` subscription's queue survives every
    /// consumer disconnecting, which is what almost every subscription in this system actually wants
    /// (`messaging.md`'s whole topic table). Equivalent to calling the <see cref="QueueLifetime"/>
    /// overload below with <see cref="QueueLifetime.Durable"/>.
    /// </summary>
    Task SubscribeAsync(
        string topic,
        SubscriptionMode mode,
        string consumerName,
        RetryPolicy retryPolicy,
        Func<EventEnvelope, IMessageContext, CancellationToken, Task> handler,
        CancellationToken cancellationToken);

    /// <summary>
    /// `15-15`: the overload a caller reaches for only when its consumer name already names something
    /// with no life beyond one process (a pod, a connection) - see <see cref="QueueLifetime"/>'s own
    /// remarks for why this is a separate, explicit parameter rather than inferred from the name
    /// string or swept up after the fact. A second interface member instead of a default parameter on
    /// the one above, specifically so every existing call site - in this repository and in `ago-chat`
    /// - keeps compiling unedited: an <see cref="Ago.Platform.Abstractions.RetryPolicy"/>-shaped
    /// optional parameter cannot sit before the required, always-explicit
    /// <paramref name="cancellationToken"/> without either reordering it (rewriting every call site)
    /// or making the token itself optional (which this codebase never does - a token is always passed
    /// explicitly).
    /// </summary>
    Task SubscribeAsync(
        string topic,
        SubscriptionMode mode,
        string consumerName,
        RetryPolicy retryPolicy,
        QueueLifetime queueLifetime,
        Func<EventEnvelope, IMessageContext, CancellationToken, Task> handler,
        CancellationToken cancellationToken);
}
