using Ago.Platform.Abstractions;

namespace Ago.Platform.Realtime;

/// <summary>
/// The topic convention both halves of the fan-out path agree on without either needing to be told
/// the other's choice: a node's own topic name is a pure function of its <see cref="NodeId"/>.
/// <see cref="NodeFanoutPublisher"/> and <see cref="NodeDeliveryConsumer"/> are the only two callers,
/// and each derives the string independently - there is no third place this could drift from.
/// </summary>
public static class NodeTopics
{
    public static string For(NodeId nodeId) => $"deliver-to-connections.{nodeId.Value}";
}
