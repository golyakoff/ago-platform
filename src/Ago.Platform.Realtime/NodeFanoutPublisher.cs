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
/// </summary>
public sealed class NodeFanoutPublisher(
    IConnectionRegistry registry,
    IEventPublisher publisher,
    IClock clock) : INodeFanoutPublisher
{
    public async Task PublishAsync(
        IReadOnlyCollection<PrincipalKey> recipients,
        string method,
        string payloadJson,
        Guid correlationId,
        CancellationToken cancellationToken)
    {
        var byNode = new Dictionary<NodeId, List<ConnectionId>>();

        foreach (var recipient in recipients)
        {
            var connections = await registry.GetConnectionsAsync(recipient, cancellationToken);
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
    }
}
