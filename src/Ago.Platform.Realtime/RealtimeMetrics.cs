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
///
/// `7-08`: the dispatch counter below is the one number the fan-out path never had - what fraction
/// of the deliveries a node was handed actually met a connection it still holds. It is a counter,
/// not a gauge, because a dispatch is an event and not a set; the same discipline still applies,
/// though - its value comes from what <see cref="ILocalConnectionDispatcher"/> itself reports about
/// each call, never from a proxy for that fact maintained beside it.
/// </summary>
public static class RealtimeMetrics
{
    public const string MeterName = "Ago.Platform.Realtime";

    public const string ConnectionsInstrumentName = "ago.platform.realtime.connections";

    public const string DispatchesInstrumentName = "ago.platform.realtime.dispatches";

    /// <summary>The dispatcher held the connection and pushed to its transport.</summary>
    public const string DeliveredOutcome = "delivered";

    /// <summary>The dispatcher does not hold that connection any more - realtime.md's harmless
    /// stale-entry no-op. Named for what is actually known ("this node has no such connection"),
    /// not for what is merely likely ("the client is gone") - the client may well be connected to
    /// another node by now.</summary>
    public const string ConnectionNotLocalOutcome = "connection_not_local";

    /// <summary>The dispatcher threw. Rare, and not the same thing as an absent connection.</summary>
    public const string FailedOutcome = "failed";

    private static readonly Meter Meter = new(MeterName);

    private static readonly Counter<long> Dispatches = Meter.CreateCounter<long>(
        DispatchesInstrumentName,
        unit: "{dispatch}",
        description:
            "Deliveries a node's NodeDeliveryConsumer handed to its ILocalConnectionDispatcher, tagged by node and by "
            + "the outcome that dispatcher reported: delivered, connection_not_local, or failed. Counts the fan-out "
            + "path only - ConnectionDrainCoordinator's shutdown pushes use the same port and are deliberately not "
            + "counted here, so a rolling deploy does not look like a wave of deliveries.");

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

    /// <summary>
    /// `7-08`: one dispatch attempt and what came of it. Internal - <see cref="NodeDeliveryConsumer"/>
    /// is the only caller by design, and it is deliberately *not* called from
    /// <see cref="ConnectionDrainCoordinator"/>, the port's other caller, so the counter keeps
    /// describing exactly one set: deliveries this node was asked to make on the fan-out path. The
    /// node tag matches the connections gauge's, so the two can be read side by side for one node.
    /// </summary>
    internal static void RecordDispatch(NodeId nodeId, string outcome) =>
        Dispatches.Add(
            1,
            new KeyValuePair<string, object?>("node", nodeId.Value),
            new KeyValuePair<string, object?>("outcome", outcome));
}
