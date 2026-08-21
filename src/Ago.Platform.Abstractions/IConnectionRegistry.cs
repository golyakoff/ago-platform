namespace Ago.Platform.Abstractions;

/// <summary>
/// "Who is connected where" - the registry `realtime.md`'s Connection registry section and `adr/0007`
/// describe: any node may own any connection, and delivery is routed by looking that ownership up.
/// Contents are **advice, not truth** (`adr/0009`) - a stale or missing entry means a harmless failed
/// delivery and a client reconnect, never a correctness bug. <see cref="RegisterAsync"/> doubles as
/// the heartbeat refresh: calling it again for a connection that is already registered is exactly
/// what keeps its entry alive, not a separate operation.
/// </summary>
public interface IConnectionRegistry
{
    /// <summary>Registers a connection, or refreshes it if already registered - both are the same
    /// idempotent "extend the TTL" operation from the registry's point of view.</summary>
    Task RegisterAsync(ConnectionId connectionId, NodeId nodeId, PrincipalKey principal, CancellationToken cancellationToken);

    /// <summary>Removes one connection immediately, e.g. on graceful disconnect - the fast path;
    /// TTL expiry is the correctness backstop if this is never called (a crash, a network
    /// partition).</summary>
    Task UnregisterAsync(ConnectionId connectionId, NodeId nodeId, PrincipalKey principal, CancellationToken cancellationToken);

    /// <summary>All connections currently on record for a principal, across every node. Entries
    /// whose own TTL has lapsed are never returned, even if a derived index has not caught up yet -
    /// a caller never sees a connection this method cannot vouch is still live enough to be worth
    /// trying.</summary>
    Task<IReadOnlyCollection<RegisteredConnection>> GetConnectionsAsync(PrincipalKey principal, CancellationToken cancellationToken);

    /// <summary>Removes every connection a node owns, e.g. on that node's own graceful shutdown
    /// (`realtime.md`: "On graceful shutdown a node deletes its own entries"). A crashed node's
    /// entries are never cleaned up this way - they simply expire.</summary>
    Task RemoveNodeAsync(NodeId nodeId, CancellationToken cancellationToken);
}
