using System.Diagnostics;
using System.Text.Json;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;

namespace Ago.Platform.Realtime;

/// <summary>
/// realtime.md's Fan-out path, steps 2-3: resolve recipients' connections, group by node, publish
/// one <see cref="NodeDelivery"/> per node. Calls <see cref="IEventPublisher.PublishAsync"/> directly
/// - not through the outbox - because a delivery notification describes no committed state change;
/// <c>MessageAccepted</c> (the event that triggered this) already went through the outbox for the
/// write it actually protects. Losing this publish is the same accepted, documented failure mode as
/// a stale registry entry (realtime.md: "advice, not truth") - the recipient still gets the message
/// on reconnect (3-03). See `adr/0020` for the fuller justification of calling
/// <see cref="IEventPublisher"/> from here at all, given its own doc comment names the outbox
/// dispatcher as the only caller.
///
/// `7-08`: this is the one place that knows how many connections a fan-out actually resolved, and
/// until now it kept the number. It records it two ways, both derived from the lists it just built
/// rather than from a counter maintained alongside them (`7-07`'s lesson): as attributes on the span
/// that already brackets this hop, and as a <see cref="FanoutResult"/> returned to the product.
/// </summary>
public sealed class NodeFanoutPublisher(
    IConnectionRegistry registry,
    IEventPublisher publisher,
    IClock clock) : INodeFanoutPublisher
{
    // `7-08`: the fan-out's own span attributes. Set on the span that is already current - the
    // "{topic} process" span RabbitMqEventConsumer started around the consumer handler that called
    // into here (`7-01`) - rather than on a new child span of this class's own: this hop has no
    // timing worth isolating from the handler that contains it, and one extra span per fan-out per
    // message is a real cost at this volume for no extra correlation. Captured at entry, not read
    // again at the end, because IEventPublisher.PublishAsync starts and stops producer spans of its
    // own in between.
    private const string RecipientsAttribute = "ago.fanout.recipients";
    private const string ConnectionsAttribute = "ago.fanout.connections";
    private const string NodesAttribute = "ago.fanout.nodes";

    public async Task<FanoutResult> PublishAsync(
        IReadOnlyCollection<PrincipalKey> recipients,
        string method,
        string payloadJson,
        Guid correlationId,
        CancellationToken cancellationToken)
    {
        var fanoutSpan = Activity.Current;
        var byNode = new Dictionary<NodeId, List<ConnectionId>>();
        var resolved = new List<ResolvedRecipient>(recipients.Count);

        foreach (var recipient in recipients)
        {
            var connections = await registry.GetConnectionsAsync(recipient, cancellationToken);
            resolved.Add(new ResolvedRecipient(recipient, connections.Count));
            foreach (var connection in connections)
            {
                if (!byNode.TryGetValue(connection.NodeId, out var list))
                {
                    list = [];
                    byNode[connection.NodeId] = list;
                }

                list.Add(connection.ConnectionId);
            }
        }

        var result = new FanoutResult(resolved);

        // Every one of these is counted off a list this method just built, so there is no second
        // number that could drift from the first: `recipients` is what the caller asked for,
        // `connections` is what the registry answered with (three open tabs is three, not one), and
        // `nodes` is how many NodeDelivery messages the loop below is about to publish.
        fanoutSpan?.SetTag(RecipientsAttribute, resolved.Count);
        fanoutSpan?.SetTag(ConnectionsAttribute, result.TotalConnections);
        fanoutSpan?.SetTag(NodesAttribute, byNode.Count);

        foreach (var (nodeId, connectionIds) in byNode)
        {
            var delivery = new NodeDelivery(connectionIds, method, payloadJson);
            var envelope = new EventEnvelope(
                MessageId: Guid.NewGuid(),
                Type: NodeTopics.For(nodeId),
                Version: 1,
                PartitionKey: nodeId.Value,
                OccurredAt: clock.UtcNow,
                CorrelationId: correlationId,
                Payload: JsonSerializer.Serialize(delivery));

            await publisher.PublishAsync(envelope, cancellationToken);
        }

        return result;
    }
}
