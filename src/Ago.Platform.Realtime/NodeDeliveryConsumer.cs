using System.Diagnostics;
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
/// names <see cref="SubscriptionMode.Competing"/>'s queue after the topic itself (stable), while
/// <see cref="SubscriptionMode.Broadcast"/> gives every subscription attempt a fresh random-suffixed
/// queue - on this node's own dedicated topic, a fresh name every restart would mean a fresh queue to
/// bind every restart, which is worse than the stable name this needs regardless of lifetime.
///
/// `15-15`: <see cref="QueueLifetime.ProcessScoped"/>, not the `Durable` every other `Competing`
/// subscription in this system needs. A node's own topic (<see cref="NodeTopics.For"/>) is unique per
/// pod and nothing will ever reattach to `deliver-to-connections.&lt;that pod&gt;` once the pod is
/// gone - measured on the live broker as 71 of 72 such queues belonging to pods that no longer
/// existed, a running total of every restart the cluster had ever had, each still bound to the fanout
/// exchange and routed into on every publish. `Durable` was the only shape available before this item
/// and was never a decision, only what the platform's only `Competing` primitive happened to do -
/// `ProcessScoped` is what "deliver to the node holding this connection" actually means: once this
/// node's own process is gone, so is everyone who could ever have used this queue.
///
/// Every delivery is acknowledged regardless of per-connection outcome:
/// <see cref="ILocalConnectionDispatcher"/> already treats an unreachable connection as a no-op
/// (realtime.md: "a stale entry causes a harmless failed delivery"), so there is never a reason to
/// requeue this message - unlike a consumer whose work is only correct once retried to success
/// (`Ago.Chat.Worker`'s <c>UnreadCounterConsumer</c>, for contrast), nothing here is retried into
/// correctness.
///
/// `7-08`: acknowledging regardless is unchanged - what changed is that the per-connection outcome
/// is no longer discarded. Each dispatch reports whether this node still held the connection
/// (<see cref="DispatchOutcome"/>), and that becomes both a span attribute and one point on
/// <see cref="RealtimeMetrics.DispatchesInstrumentName"/>. A redelivered <see cref="NodeDelivery"/>
/// counts again, deliberately: the instrument describes dispatch *attempts this node made*, and a
/// redelivery is a second real attempt, not a double-count of the first.
/// </summary>
public sealed class NodeDeliveryConsumer(
    IEventConsumer consumer,
    ILocalConnectionDispatcher dispatcher,
    NodeId currentNode,
    ILogger<NodeDeliveryConsumer> logger) : BackgroundService
{
    // `7-01`: the last hop the trace must reach (this item's own backlog wording) - "Ago.Platform.
    // Realtime" rather than a product name, same reasoning as RabbitMqTracing's own ActivitySource:
    // this class runs for any product built on the platform, so it names its own spans generically
    // and is picked up by AddPlatformObservability's "Ago.*" wildcard, never by a literal reference.
    private static readonly ActivitySource ActivitySource = new("Ago.Platform.Realtime");

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var topic = NodeTopics.For(currentNode);

        // `15-15`: shared across every node rather than `$"{topic}.dlq"` (per-pod, as this used to
        // read) - the DLQ itself stays durable regardless of QueueLifetime (RabbitMqEventConsumer's
        // own remarks), so a per-node name would still have accumulated one durable, empty, never-
        // cleaned queue per pod, the identical orphan this item measured just one queue over. A single
        // stable name that every node's subscription declares identically is safe precisely because
        // this handler never actually dead-letters into it (below: MaxAttempts: 1, and the catch block
        // acks rather than lets a failure reach a retry/DLQ decision) - if that ever changes, this name
        // stops being able to stay generic across nodes and needs its own design.
        var retryPolicy = new RetryPolicy(MaxAttempts: 1, InitialBackoff: TimeSpan.Zero, DeadLetterName: "deliver-to-connections.dlq");

        // `5-11`: a stable name, even though nothing else subscribes to this node's own topic today -
        // correct by construction rather than by the accident of being the only subscriber, the same
        // discipline this fix asks of every other Competing subscription. `15-15`: ProcessScoped,
        // because "node-delivery" on *this* topic is precisely the consumer name this item's own
        // QueueLifetime doc comment describes - one with no life beyond this process.
        return consumer.SubscribeAsync(
            topic, SubscriptionMode.Competing, "node-delivery", retryPolicy, QueueLifetime.ProcessScoped, HandleAsync, stoppingToken);
    }

    private async Task HandleAsync(EventEnvelope envelope, IMessageContext context, CancellationToken cancellationToken)
    {
        try
        {
            var delivery = JsonSerializer.Deserialize<NodeDelivery>(envelope.Payload)
                ?? throw new InvalidOperationException($"Could not deserialize {nameof(NodeDelivery)} for {envelope.MessageId}.");

            foreach (var connectionId in delivery.ConnectionIds)
            {
                // A child of whatever Activity is current (the "{topic} process" span
                // RabbitMqEventConsumer already started around this whole handler, itself parented
                // from the outbox dispatch that published this delivery) - one span per connection,
                // since one NodeDelivery can fan out to several connections belonging to the same
                // recipient (several open tabs), each worth its own timing.
                using var activity = ActivitySource.StartActivity("node_delivery.dispatch_to_connection", ActivityKind.Producer);
                activity?.SetTag("ago.connection_id", connectionId.Value);
                string outcome;
                try
                {
                    var dispatched = await dispatcher.DispatchAsync(connectionId, delivery.Method, delivery.PayloadJson, cancellationToken);
                    outcome = dispatched == DispatchOutcome.Delivered
                        ? RealtimeMetrics.DeliveredOutcome
                        : RealtimeMetrics.ConnectionNotLocalOutcome;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // One connection failing (gone, transport error) must not stop the rest of this
                    // node's batch from being delivered - realtime.md's "harmless failed delivery"
                    // applies per connection, not per envelope.
                    logger.LogDebug(ex, "Failed to dispatch to connection {ConnectionId} - continuing with the rest of the batch.", connectionId);
                    outcome = RealtimeMetrics.FailedOutcome;
                }

                // Recorded after the try/catch rather than inside it, so every path through this
                // loop lands on exactly one point and no path lands on two.
                activity?.SetTag("ago.dispatch.outcome", outcome);
                RealtimeMetrics.RecordDispatch(currentNode, outcome);
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
