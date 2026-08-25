using Ago.Platform.Abstractions;
using Ago.Platform.Realtime;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenTelemetry;
using OpenTelemetry.Metrics;

namespace Ago.Platform.Integration.Tests;

/// <summary>
/// `7-07`: the gauge `7-02` shipped counted <see cref="IConnectionRegistry.RegisterAsync"/> calls,
/// and <see cref="ConnectionHeartbeat"/> calls that method every ten seconds for every connection a
/// node still holds - so on an idle deployment the value climbed forever. These tests exercise the
/// real <see cref="RedisConnectionRegistry"/> against the shared container and the real
/// <see cref="ConnectionHeartbeat"/> background service, because that pairing *is* the defect: no fake registry could
/// have reproduced it, which is precisely why it survived unnoticed for as long as the heartbeat has
/// existed.
/// </summary>
[Collection(RedisCollection.Name)]
public sealed class RealtimeConnectionsGaugeTests(RedisFixture fixture)
{
    [Fact]
    public async Task HeartbeatCycles_OverAStableConnectionSet_LeaveTheGaugeAtTheRealConnectionCount()
    {
        var exportedMetrics = new List<Metric>();
        using var meterProvider = Sdk.CreateMeterProviderBuilder()
            .AddMeter(RealtimeMetrics.MeterName)
            .AddInMemoryExporter(exportedMetrics)
            .Build();

        var registry = CreateRegistry();
        var nodeId = new NodeId($"node-{Guid.NewGuid():N}");
        var tracker = new LocalConnectionTracker();
        RealtimeMetrics.TrackNode(nodeId, tracker); // what AddConnectionRegistry does for a real host

        // Three connections, registered exactly the way a hub's OnConnectedAsync does it
        // (Ago.Chat.Api's HubConnectionRegistration: tracker first, then the registry).
        var connections = Enumerable.Range(0, 3)
            .Select(_ => (Connection: new ConnectionId(Guid.NewGuid().ToString()), Principal: new PrincipalKey($"visitor:{Guid.NewGuid()}")))
            .ToArray();
        foreach (var (connectionId, principal) in connections)
        {
            tracker.Add(connectionId, principal);
            await registry.RegisterAsync(connectionId, nodeId, principal, CancellationToken.None);
        }

        Assert.Equal(3, ReadGaugeValue(meterProvider, exportedMetrics, nodeId.Value));

        // Five full heartbeat cycles over that same, unchanged set - fifteen RegisterAsync calls
        // that are refreshes, not new connections. Awaited on the call count rather than on wall
        // clock so the test is deterministic (testing.md: no sleep-and-hope).
        const int RefreshesToAwait = 5 * 3;
        var counting = new RefreshCountingRegistry(registry, RefreshesToAwait);
        var heartbeat = new ConnectionHeartbeat(
            counting,
            tracker,
            nodeId,
            Options.Create(new ConnectionHeartbeatOptions { Interval = TimeSpan.FromMilliseconds(20) }),
            NullLogger<ConnectionHeartbeat>.Instance);

        await heartbeat.StartAsync(CancellationToken.None);
        try
        {
            await counting.RefreshesObserved.WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            await heartbeat.StopAsync(CancellationToken.None);
        }

        // The node still holds exactly three connections, and the registry's own node set agrees -
        // `7-07`'s Done-when, checked here rather than only by eye on a live deployment.
        var nodeSetLength = await fixture.Multiplexer.GetDatabase().SetLengthAsync($"node:{nodeId.Value}:conns");
        Assert.Equal(3, nodeSetLength);
        Assert.Equal(3, ReadGaugeValue(meterProvider, exportedMetrics, nodeId.Value));
    }

    /// <summary>
    /// `7-02`'s Done-when ("prove at least one real value change per instrument"), kept but moved
    /// onto the set the gauge now actually describes: a connection appears when the node starts
    /// holding it and disappears when it stops, with the registry no longer in the loop at all.
    /// </summary>
    [Fact]
    public async Task AConnectionAppearingAndGoingAway_MovesTheGauge()
    {
        var exportedMetrics = new List<Metric>();
        using var meterProvider = Sdk.CreateMeterProviderBuilder()
            .AddMeter(RealtimeMetrics.MeterName)
            .AddInMemoryExporter(exportedMetrics)
            .Build();

        var registry = CreateRegistry();
        var nodeId = new NodeId($"node-{Guid.NewGuid():N}");
        var tracker = new LocalConnectionTracker();
        RealtimeMetrics.TrackNode(nodeId, tracker);
        var principal = new PrincipalKey($"visitor:{Guid.NewGuid()}");
        var first = new ConnectionId(Guid.NewGuid().ToString());
        var second = new ConnectionId(Guid.NewGuid().ToString());

        tracker.Add(first, principal);
        await registry.RegisterAsync(first, nodeId, principal, CancellationToken.None);
        tracker.Add(second, principal);
        await registry.RegisterAsync(second, nodeId, principal, CancellationToken.None);
        Assert.Equal(2, ReadGaugeValue(meterProvider, exportedMetrics, nodeId.Value));

        tracker.Remove(first);
        await registry.UnregisterAsync(first, nodeId, principal, CancellationToken.None);
        Assert.Equal(1, ReadGaugeValue(meterProvider, exportedMetrics, nodeId.Value));
    }

    private static long ReadGaugeValue(MeterProvider meterProvider, List<Metric> exportedMetrics, string nodeId)
    {
        exportedMetrics.Clear();
        meterProvider.ForceFlush();
        var gauge = exportedMetrics.Single(m => m.Name == RealtimeMetrics.ConnectionsInstrumentName);

        foreach (ref readonly var point in gauge.GetMetricPoints())
        {
            foreach (var tag in point.Tags)
            {
                if (tag.Key == "node" && (string?)tag.Value == nodeId)
                {
                    return point.GetGaugeLastValueLong();
                }
            }
        }

        throw new InvalidOperationException($"No gauge point found for node {nodeId}.");
    }

    private RedisConnectionRegistry CreateRegistry(TimeSpan? ttl = null) =>
        new(fixture.Multiplexer,
            Options.Create(new ConnectionRegistryOptions { EntryTtl = ttl ?? TimeSpan.FromSeconds(30) }),
            NullLogger<RedisConnectionRegistry>.Instance);

    /// <summary>Counts <see cref="RegisterAsync"/> calls and completes once the heartbeat has made
    /// enough of them, so the test waits on the thing it cares about instead of on a timer.</summary>
    private sealed class RefreshCountingRegistry(IConnectionRegistry inner, int refreshesToAwait) : IConnectionRegistry
    {
        private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _registerCalls;

        public Task RefreshesObserved => _reached.Task;

        public Task RegisterAsync(ConnectionId connectionId, NodeId nodeId, PrincipalKey principal, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _registerCalls) >= refreshesToAwait)
            {
                _reached.TrySetResult();
            }

            return inner.RegisterAsync(connectionId, nodeId, principal, cancellationToken);
        }

        public Task UnregisterAsync(ConnectionId connectionId, NodeId nodeId, PrincipalKey principal, CancellationToken cancellationToken) =>
            inner.UnregisterAsync(connectionId, nodeId, principal, cancellationToken);

        public Task<IReadOnlyCollection<RegisteredConnection>> GetConnectionsAsync(PrincipalKey principal, CancellationToken cancellationToken) =>
            inner.GetConnectionsAsync(principal, cancellationToken);

        public Task RemoveNodeAsync(NodeId nodeId, CancellationToken cancellationToken) =>
            inner.RemoveNodeAsync(nodeId, cancellationToken);
    }
}
