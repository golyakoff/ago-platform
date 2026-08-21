namespace Ago.Platform.Abstractions;

/// <summary>
/// One node's share of a fan-out: "invoke this named method with this JSON payload on each of these
/// local connections" (realtime.md's Fan-out path). Deliberately domain-free - <see cref="Method"/>
/// and <see cref="PayloadJson"/> are opaque to everything except the caller and the eventual local
/// dispatcher; nothing here knows a message, a visitor or a hub exists.
/// </summary>
public sealed record NodeDelivery(IReadOnlyList<ConnectionId> ConnectionIds, string Method, string PayloadJson);
