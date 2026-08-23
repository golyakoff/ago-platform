using System.Text.Json;
using Ago.Platform.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ago.Platform.Realtime;

/// <summary>
/// realtime.md's Fan-out path, step 4: consumes only this node's own topic and pushes to local
/// connections via <see cref="ILocalConnectionDispatcher"/> - the host-supplied implementation that
/// actually knows how to reach a connection (SignalR, in `Ago.Chat.Api`'s case).
///
/// <see cref="SubscriptionMode.Competing"/>, not <see cref="SubscriptionMode.Broadcast"/>, even
/// though exactly one process ever subscribes to a given node's topic: <c>RabbitMqEventConsumer</c>
/// names <see cref="SubscriptionMode.Competing"/>'s queue after the topic itself (stable, durable),
/// while <see cref="SubscriptionMode.Broadcast"/> gives every subscription attempt a fresh
/// random-suffixed queue *and* a durable, never-auto-deleted retry queue to match - on this node's
/// own dedicated topic, restarting with <see cref="SubscriptionMode.Broadcast"/> would leak one such
/// retry queue per restart. <see cref="SubscriptionMode.Competing"/>'s stable name leaks at most once
/// per node *replacement* (a new pod identity, not just a reconnect) - accepted, not solved, here;
/// nothing in this project has a queue-retention policy yet.
///
/// Every delivery is acknowledged regardless of per-connection outcome:
/// <see cref="ILocalConnectionDispatcher"/> already treats an unreachable connection as a no-op
/// (realtime.md: "a stale entry causes a harmless failed delivery"), so there is never a reason to
/// requeue this message - unlike a consumer whose work is only correct once retried to success
/// (`Ago.Chat.Worker`'s <c>UnreadCounterConsumer</c>, for contrast), nothing here is retried into
/// correctness.
/// </summary>
public sealed class NodeDeliveryConsumer(
    IEventConsumer consumer,
    ILocalConnectionDispatcher dispatcher,
    NodeId currentNode,
    ILogger<NodeDeliveryConsumer> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var topic = NodeTopics.For(currentNode);
        var retryPolicy = new RetryPolicy(MaxAttempts: 1, InitialBackoff: TimeSpan.Zero, DeadLetterName: $"{topic}.dlq");

        // `5-11`: a stable name, even though nothing else subscribes to this node's own topic today -
        // correct by construction rather than by the accident of being the only subscriber, the same
        // discipline this fix asks of every other Competing subscription.
        return consumer.SubscribeAsync(topic, SubscriptionMode.Competing, "node-delivery", retryPolicy, HandleAsync, stoppingToken);
    }

    private async Task HandleAsync(EventEnvelope envelope, IMessageContext context, CancellationToken cancellationToken)
    {
        try
        {
            var delivery = JsonSerializer.Deserialize<NodeDelivery>(envelope.Payload)
                ?? throw new InvalidOperationException($"Could not deserialize {nameof(NodeDelivery)} for {envelope.MessageId}.");

            foreach (var connectionId in delivery.ConnectionIds)
            {
                try
                {
                    await dispatcher.DispatchAsync(connectionId, delivery.Method, delivery.PayloadJson, cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // One connection failing (gone, transport error) must not stop the rest of this
                    // node's batch from being delivered - realtime.md's "harmless failed delivery"
                    // applies per connection, not per envelope.
                    logger.LogDebug(ex, "Failed to dispatch to connection {ConnectionId} - continuing with the rest of the batch.", connectionId);
                }
            }

            await context.AckAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to process node delivery {MessageId}.", envelope.MessageId);
            await context.AckAsync(cancellationToken); // best-effort, ephemeral - never worth a requeue loop
        }
    }
}
