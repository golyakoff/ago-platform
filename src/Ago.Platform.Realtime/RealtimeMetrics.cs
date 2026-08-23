using System.Collections.Concurrent;
using System.Diagnostics.Metrics;

namespace Ago.Platform.Realtime;

/// <summary>
/// `7-02`: nfr.md's "connection count per node" - <see cref="RedisConnectionRegistry"/>'s own local
/// bookkeeping of the connections *this* node has registered, not a live Redis query at collection
/// time: an <see cref="ObservableGauge{T}"/> callback runs synchronously and must not block on
/// network I/O, and a registry read failing (realtime.md's own "advice, not truth" contract) must
/// never be the reason a metrics scrape fails either. <see cref="RedisConnectionRegistry.RegisterAsync"/>/
/// <see cref="RedisConnectionRegistry.UnregisterAsync"/>/<see cref="RedisConnectionRegistry.RemoveNodeAsync"/>
/// update the counters below unconditionally, before attempting the Redis write - this metric
/// describes what this node believes about its own connections, the same locally-sourced-of-truth
/// reasoning realtime.md already applies to registry failure handling, just extended to a gauge
/// instead of a log line.
/// </summary>
public static class RealtimeMetrics
{
    public const string MeterName = "Ago.Platform.Realtime";

    public const string ConnectionsInstrumentName = "ago.platform.realtime.connections";

    private static readonly Meter Meter = new(MeterName);

    private static readonly ConcurrentDictionary<string, long> ConnectionsByNode = new();

    static RealtimeMetrics()
    {
        Meter.CreateObservableGauge(
            ConnectionsInstrumentName,
            () => ConnectionsByNode.Select(kvp => new Measurement<long>(
                kvp.Value, new KeyValuePair<string, object?>("node", kvp.Key))),
            unit: "{connection}",
            description: "Connections this platform's connection registry believes each node currently holds.");
    }

    internal static void ConnectionRegistered(string nodeId) =>
        ConnectionsByNode.AddOrUpdate(nodeId, 1, static (_, current) => current + 1);

    internal static void ConnectionUnregistered(string nodeId) =>
        ConnectionsByNode.AddOrUpdate(nodeId, 0, static (_, current) => Math.Max(0, current - 1));

    internal static void NodeRemoved(string nodeId) => ConnectionsByNode[nodeId] = 0;
}
