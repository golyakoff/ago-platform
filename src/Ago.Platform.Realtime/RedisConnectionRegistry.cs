using Ago.Platform.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Ago.Platform.Realtime;

/// <summary>
/// Redis-backed <see cref="IConnectionRegistry"/>, keyed exactly as realtime.md's Connection
/// registry section specifies: <c>conn:{connection_id}</c> (a hash: node, principal),
/// <c>presence:{principal}</c> (a set of connection ids - <c>PrincipalKey</c> already carries the
/// "visitor:"/"operator:" namespace a caller chose, so this reproduces
/// <c>presence:visitor:{id}</c>/<c>presence:operator:{id}</c> without the registry itself knowing
/// either concept exists), and <c>node:{node_id}:conns</c> (a set, for bulk cleanup).
///
/// Every method swallows <see cref="RedisException"/> and logs a warning rather than throwing:
/// realtime.md's own failure table says a Redis outage must mean "new connections still accepted,
/// cross-node delivery degrades" - a registry write or read that fails must never be the reason a
/// connection is rejected or a hub method throws. This is the same "advice, not truth" contract
/// applied to error handling, not just to staleness.
/// </summary>
public sealed class RedisConnectionRegistry(
    IConnectionMultiplexer multiplexer,
    IOptions<ConnectionRegistryOptions> options,
    ILogger<RedisConnectionRegistry> logger) : IConnectionRegistry
{
    private const string NodeField = "node";
    private const string PrincipalField = "principal";

    public async Task RegisterAsync(ConnectionId connectionId, NodeId nodeId, PrincipalKey principal, CancellationToken cancellationToken)
    {
        var db = multiplexer.GetDatabase();
        var ttl = options.Value.EntryTtl;
        var connKey = ConnKey(connectionId);
        var presenceKey = PresenceKey(principal);
        var nodeKey = NodeKey(nodeId);

        try
        {
            // Two round trips (write, then expire) rather than a Lua script: this registry is
            // advice, not truth, so a rare partial write under a race is harmless - TTL either
            // lands or the whole entry is gone shortly, never a half-alive one that outlives its
            // intended window (realtime.md).
            await Task.WhenAll(
                db.HashSetAsync(connKey, [new HashEntry(NodeField, nodeId.Value), new HashEntry(PrincipalField, principal.Value)]).WaitAsync(cancellationToken),
                db.SetAddAsync(presenceKey, connectionId.Value).WaitAsync(cancellationToken),
                db.SetAddAsync(nodeKey, connectionId.Value).WaitAsync(cancellationToken));

            await Task.WhenAll(
                db.KeyExpireAsync(connKey, ttl).WaitAsync(cancellationToken),
                db.KeyExpireAsync(presenceKey, ttl).WaitAsync(cancellationToken),
                db.KeyExpireAsync(nodeKey, ttl).WaitAsync(cancellationToken));
        }
        catch (RedisException ex)
        {
            logger.LogWarning(ex,
                "Failed to register connection {ConnectionId} in the connection registry - continuing without it.",
                connectionId);
        }
    }

    public async Task UnregisterAsync(ConnectionId connectionId, NodeId nodeId, PrincipalKey principal, CancellationToken cancellationToken)
    {
        var db = multiplexer.GetDatabase();

        try
        {
            await Task.WhenAll(
                db.KeyDeleteAsync(ConnKey(connectionId)).WaitAsync(cancellationToken),
                db.SetRemoveAsync(PresenceKey(principal), connectionId.Value).WaitAsync(cancellationToken),
                db.SetRemoveAsync(NodeKey(nodeId), connectionId.Value).WaitAsync(cancellationToken));
        }
        catch (RedisException ex)
        {
            logger.LogWarning(ex,
                "Failed to unregister connection {ConnectionId} - its entry will expire on its own.",
                connectionId);
        }
    }

    public async Task<IReadOnlyCollection<RegisteredConnection>> GetConnectionsAsync(PrincipalKey principal, CancellationToken cancellationToken)
    {
        var db = multiplexer.GetDatabase();
        var presenceKey = PresenceKey(principal);

        try
        {
            var members = await db.SetMembersAsync(presenceKey).WaitAsync(cancellationToken);
            if (members.Length == 0)
            {
                return [];
            }

            var lookups = members.Select(async member =>
            {
                var connectionId = new ConnectionId(member.ToString());
                var hash = await db.HashGetAllAsync(ConnKey(connectionId)).WaitAsync(cancellationToken);
                if (hash.Length == 0)
                {
                    // Listed in the presence set but its own entry already expired - opportunistic
                    // cleanup, a fast path only (realtime.md: "cleanup is a fast path, not the
                    // correctness mechanism"). Fire-and-forget is fine here specifically because a
                    // missed cleanup is corrected by this same lazy path next time, or by the
                    // presence set's own TTL eventually.
                    _ = db.SetRemoveAsync(presenceKey, member);
                    return (RegisteredConnection?)null;
                }

                var nodeEntry = hash.First(h => h.Name == NodeField);
                return new RegisteredConnection(connectionId, new NodeId(nodeEntry.Value.ToString()));
            });

            var results = await Task.WhenAll(lookups);
            return [.. results.Where(r => r is not null).Select(r => r!)];
        }
        catch (RedisException ex)
        {
            logger.LogWarning(ex,
                "Failed to read connections for principal {Principal} - treating as none known.",
                principal);
            return [];
        }
    }

    public async Task RemoveNodeAsync(NodeId nodeId, CancellationToken cancellationToken)
    {
        var db = multiplexer.GetDatabase();
        var nodeKey = NodeKey(nodeId);

        try
        {
            var members = await db.SetMembersAsync(nodeKey).WaitAsync(cancellationToken);

            await Task.WhenAll(members.Select(async member =>
            {
                var connKey = ConnKey(new ConnectionId(member.ToString()));
                var hash = await db.HashGetAllAsync(connKey).WaitAsync(cancellationToken);
                var cleanup = new List<Task> { db.KeyDeleteAsync(connKey) };
                var principalEntry = hash.FirstOrDefault(h => h.Name == PrincipalField);
                if (!principalEntry.Value.IsNullOrEmpty)
                {
                    cleanup.Add(db.SetRemoveAsync(PresenceKey(new PrincipalKey(principalEntry.Value.ToString())), member));
                }

                await Task.WhenAll(cleanup);
            }));

            await db.KeyDeleteAsync(nodeKey).WaitAsync(cancellationToken);
        }
        catch (RedisException ex)
        {
            logger.LogWarning(ex,
                "Failed to remove node {NodeId}'s connection registry entries - they will expire on their own.",
                nodeId);
        }
    }

    private static string ConnKey(ConnectionId connectionId) => $"conn:{connectionId.Value}";

    private static string PresenceKey(PrincipalKey principal) => $"presence:{principal.Value}";

    private static string NodeKey(NodeId nodeId) => $"node:{nodeId.Value}:conns";
}
