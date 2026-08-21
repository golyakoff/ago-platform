namespace Ago.Platform.Abstractions;

/// <summary>One connection currently on record for a <see cref="PrincipalKey"/>, and which node
/// owns it - everything a caller needs to route a delivery (realtime.md's Fan-out path).</summary>
public sealed record RegisteredConnection(ConnectionId ConnectionId, NodeId NodeId);
