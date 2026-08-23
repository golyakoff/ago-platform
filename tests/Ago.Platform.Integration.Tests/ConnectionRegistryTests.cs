using Ago.Platform.Abstractions;
using Ago.Platform.Realtime;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenTelemetry;
using OpenTelemetry.Metrics;

namespace Ago.Platform.Integration.Tests;

/// <summary>3-01's backlog item: real Redis (Testcontainers), no mocking (testing.md). Every test
/// builds its own <see cref="RedisConnectionRegistry"/> against the shared container so tests can
/// use different TTLs without racing each other.</summary>
[Collection(RedisCollection.Name)]
public sealed class ConnectionRegistryTests(RedisFixture fixture)
{
    [Fact]
    public async Task RegisterThenGetConnections_ReturnsTheConnectionWithItsNode()
    {
        var registry = CreateRegistry();
        var principal = new PrincipalKey($"visitor:{Guid.NewGuid()}");
        var connectionId = new ConnectionId(Guid.NewGuid().ToString());
        var nodeId = new NodeId("node-a");

        await registry.RegisterAsync(connectionId, nodeId, principal, CancellationToken.None);
        var connections = await registry.GetConnectionsAsync(principal, CancellationToken.None);

        var connection = Assert.Single(connections);
        Assert.Equal(connectionId, connection.ConnectionId);
        Assert.Equal(nodeId, connection.NodeId);
    }

    [Fact]
    public async Task TwoConnectionsFromTheSamePrincipal_BothAppear_SimulatingAReconnectBeforeTheOldOneExpires()
    {
        var registry = CreateRegistry();
        var principal = new PrincipalKey($"visitor:{Guid.NewGuid()}");
        var oldConnection = new ConnectionId(Guid.NewGuid().ToString());
        var newConnection = new ConnectionId(Guid.NewGuid().ToString());
        var nodeId = new NodeId("node-a");

        await registry.RegisterAsync(oldConnection, nodeId, principal, CancellationToken.None);
        await registry.RegisterAsync(newConnection, nodeId, principal, CancellationToken.None); // e.g. a reconnect from a second tab

        var connections = await registry.GetConnectionsAsync(principal, CancellationToken.None);
        Assert.Equal(2, connections.Count);
        Assert.Contains(connections, c => c.ConnectionId == oldConnection);
        Assert.Contains(connections, c => c.ConnectionId == newConnection);
    }

    [Fact]
    public async Task AnEntry_WithNoHeartbeat_ExpiresOnItsOwnAndStopsAppearing()
    {
        var registry = CreateRegistry(ttl: TimeSpan.FromSeconds(1));
        var principal = new PrincipalKey($"visitor:{Guid.NewGuid()}");
        var connectionId = new ConnectionId(Guid.NewGuid().ToString());

        await registry.RegisterAsync(connectionId, new NodeId("node-a"), principal, CancellationToken.None);
        Assert.Single(await registry.GetConnectionsAsync(principal, CancellationToken.None));

        // No heartbeat refresh in between - proving TTL expiry itself, not just that the method exists.
        await Task.Delay(TimeSpan.FromSeconds(2));

        Assert.Empty(await registry.GetConnectionsAsync(principal, CancellationToken.None));
    }

    [Fact]
    public async Task UnregisterAsync_RemovesTheConnectionImmediately_WithoutWaitingForTtl()
    {
        var registry = CreateRegistry(ttl: TimeSpan.FromMinutes(5)); // long TTL - a slow disconnect path must not depend on it lapsing
        var principal = new PrincipalKey($"visitor:{Guid.NewGuid()}");
        var connectionId = new ConnectionId(Guid.NewGuid().ToString());
        var nodeId = new NodeId("node-a");

        await registry.RegisterAsync(connectionId, nodeId, principal, CancellationToken.None);
        await registry.UnregisterAsync(connectionId, nodeId, principal, CancellationToken.None);

        Assert.Empty(await registry.GetConnectionsAsync(principal, CancellationToken.None));
    }

    [Fact]
    public async Task RemoveNodeAsync_RemovesEveryConnectionThatNodeOwns_AcrossDifferentPrincipals()
    {
        var registry = CreateRegistry(ttl: TimeSpan.FromMinutes(5));
        var dyingNode = new NodeId($"node-{Guid.NewGuid():N}");
        var survivingNode = new NodeId("node-b");
        var visitor = new PrincipalKey($"visitor:{Guid.NewGuid()}");
        var operatorKey = new PrincipalKey($"operator:{Guid.NewGuid()}");
        var visitorConn = new ConnectionId(Guid.NewGuid().ToString());
        var operatorConn = new ConnectionId(Guid.NewGuid().ToString());
        var survivingConn = new ConnectionId(Guid.NewGuid().ToString());

        await registry.RegisterAsync(visitorConn, dyingNode, visitor, CancellationToken.None);
        await registry.RegisterAsync(operatorConn, dyingNode, operatorKey, CancellationToken.None);
        await registry.RegisterAsync(survivingConn, survivingNode, visitor, CancellationToken.None);

        await registry.RemoveNodeAsync(dyingNode, CancellationToken.None);

        var visitorConnections = await registry.GetConnectionsAsync(visitor, CancellationToken.None);
        Assert.DoesNotContain(visitorConnections, c => c.ConnectionId == visitorConn);
        Assert.Contains(visitorConnections, c => c.ConnectionId == survivingConn); // untouched - different node
        Assert.Empty(await registry.GetConnectionsAsync(operatorKey, CancellationToken.None));
    }

    /// <summary>
    /// `7-02`'s Done-when: proves a real value change. Registers two connections on one node, then
    /// unregisters one, and asserts RealtimeMetrics' node-tagged gauge reflects exactly one remaining
    /// - not merely that the instrument exists.
    /// </summary>
    [Fact]
    public async Task RegisterAndUnregister_MoveTheConnectionsPerNodeGauge()
    {
        var exportedMetrics = new List<Metric>();
        using var meterProvider = Sdk.CreateMeterProviderBuilder()
            .AddMeter(RealtimeMetrics.MeterName)
            .AddInMemoryExporter(exportedMetrics)
            .Build();

        var registry = CreateRegistry();
        var nodeId = new NodeId($"node-{Guid.NewGuid():N}");
        var principal = new PrincipalKey($"visitor:{Guid.NewGuid()}");
        var firstConnection = new ConnectionId(Guid.NewGuid().ToString());
        var secondConnection = new ConnectionId(Guid.NewGuid().ToString());

        await registry.RegisterAsync(firstConnection, nodeId, principal, CancellationToken.None);
        await registry.RegisterAsync(secondConnection, nodeId, principal, CancellationToken.None);
        Assert.Equal(2, ReadGaugeValue(meterProvider, exportedMetrics, nodeId.Value));

        await registry.UnregisterAsync(firstConnection, nodeId, principal, CancellationToken.None);
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
}
