using System.Collections.Concurrent;
using Ago.Platform.Abstractions;

namespace Ago.Platform.Realtime;

/// <summary>
/// What this one process currently believes it holds - purely in-memory, never Redis. Exists so
/// <see cref="ConnectionHeartbeat"/> knows which connections to keep refreshing without asking every
/// hub instance individually, and so a graceful shutdown knows exactly what to unregister. A hub
/// adds an entry in <c>OnConnectedAsync</c> and removes it in <c>OnDisconnectedAsync</c> -
/// this class does not talk to <see cref="IConnectionRegistry"/> itself, it only remembers what the
/// caller already told it to register there.
/// </summary>
public sealed class LocalConnectionTracker
{
    private readonly ConcurrentDictionary<ConnectionId, PrincipalKey> _connections = new();

    public void Add(ConnectionId connectionId, PrincipalKey principal) => _connections[connectionId] = principal;

    public void Remove(ConnectionId connectionId) => _connections.TryRemove(connectionId, out _);

    /// <summary>A point-in-time copy - safe to enumerate while connections are concurrently being
    /// added or removed (concurrency.md: shared mutable state is <c>ConcurrentDictionary</c>, and
    /// nothing here holds a lock across the enumeration).</summary>
    public IReadOnlyCollection<KeyValuePair<ConnectionId, PrincipalKey>> Snapshot() => [.. _connections];
}
