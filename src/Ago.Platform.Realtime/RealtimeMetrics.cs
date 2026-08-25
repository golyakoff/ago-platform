using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Ago.Platform.Abstractions;

namespace Ago.Platform.Realtime;

/// <summary>
/// `7-02`: nfr.md's "connection count per node" - reported from this node's own
/// <see cref="LocalConnectionTracker"/>, not a live Redis query at collection time: an
/// <see cref="ObservableGauge{T}"/> callback runs synchronously and must not block on network I/O,
/// and a registry read failing (realtime.md's own "advice, not truth" contract) must never be the
/// reason a metrics scrape fails either.
///
/// `7-07`: the gauge *reads the set it describes* rather than being incremented alongside the calls
/// that maintain it. The original shape counted <see cref="IConnectionRegistry.RegisterAsync"/>
/// calls, which was wrong the moment <see cref="ConnectionHeartbeat"/> existed - that method
/// deliberately doubles as the TTL refresh, so on an idle deployment the value climbed by one per
/// connection per heartbeat interval and never came back down. Reading
/// <see cref="LocalConnectionTracker.Count"/> at collection time removes the whole class of drift:
/// there is no second number to keep in sync with the first. This is the same shape
/// <c>ResilienceMetrics</c> already uses for the breaker-state gauge (register a live handle, read
/// it in the callback) rather than tracking transitions itself.
/// </summary>
public static class RealtimeMetrics
{
    public const string MeterName = "Ago.Platform.Realtime";

    public const string ConnectionsInstrumentName = "ago.platform.realtime.connections";

    private static readonly Meter Meter = new(MeterName);

    // Keyed by node id. One entry in a real process (a host owns exactly one NodeId and one
    // tracker); a dictionary rather than a single field only so the gauge keeps its "node" tag
    // honest in a test host that builds several nodes side by side.
    private static readonly ConcurrentDictionary<string, LocalConnectionTracker> TrackersByNode = new();

    static RealtimeMetrics()
    {
        Meter.CreateObservableGauge(
            ConnectionsInstrumentName,
            () => TrackersByNode.Select(kvp => new Measurement<long>(
                kvp.Value.Count, new KeyValuePair<string, object?>("node", kvp.Key))),
            unit: "{connection}",
            description: "Connections each node currently holds, as that node's own connection tracker sees them.");
    }

    /// <summary>
    /// Makes <paramref name="tracker"/> the gauge's source for <paramref name="nodeId"/>. Called
    /// once from <see cref="ServiceCollectionExtensions.AddConnectionRegistry"/> - composition-root
    /// wiring, the only place that knows both the node's identity and its tracker. Public rather
    /// than internal because a host or a test that composes the realtime pieces by hand, without
    /// that extension method, still has to be able to say which tracker describes which node.
    /// </summary>
    public static void TrackNode(NodeId nodeId, LocalConnectionTracker tracker) =>
        TrackersByNode[nodeId.Value] = tracker;
}
