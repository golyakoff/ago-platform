using System.Collections.Concurrent;
using Ago.Platform.Abstractions;
using Ago.Platform.Realtime;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Ago.Platform.Integration.Tests;

/// <summary>
/// `3-06`: `concurrency.md`'s graceful-shutdown sequence, real Redis (the registry). No RabbitMQ
/// needed - the dispatcher this test cares about is a hand-written fake, per testing.md ("no
/// mocking framework for ports we own" - <see cref="ILocalConnectionDispatcher"/> here plays the
/// same role <c>FakeLocalConnectionDispatcher</c> does in <c>ConnectionFanoutEndToEndTests</c>).
/// </summary>
[Collection(RedisCollection.Name)]
public sealed class ConnectionDrainCoordinatorTests(RedisFixture fixture)
{
    [Fact]
    public async Task StopAsync_TellsEveryLocalConnectionToReconnect_AndRemovesTheNodesRegistryEntries()
    {
        var nodeId = new NodeId($"node-{Guid.NewGuid():N}");
        var registry = new RedisConnectionRegistry(
            fixture.Multiplexer, Options.Create(new ConnectionRegistryOptions()), NullLogger<RedisConnectionRegistry>.Instance);
        var tracker = new LocalConnectionTracker();
        var principal = new PrincipalKey($"visitor:{Guid.NewGuid()}");
        var connectionA = new ConnectionId(Guid.NewGuid().ToString());
        var connectionB = new ConnectionId(Guid.NewGuid().ToString());
        tracker.Add(connectionA, principal);
        tracker.Add(connectionB, principal);
        await registry.RegisterAsync(connectionA, nodeId, principal, CancellationToken.None);
        await registry.RegisterAsync(connectionB, nodeId, principal, CancellationToken.None);

        var dispatcher = new FakeLocalConnectionDispatcher();
        var lifetime = new FakeHostApplicationLifetime();
        var drainState = new DrainState();
        var coordinator = new ConnectionDrainCoordinator(
            tracker, dispatcher, registry, nodeId, lifetime, drainState,
            Options.Create(new DrainOptions { DrainTimeout = TimeSpan.FromSeconds(2) }),
            NullLogger<ConnectionDrainCoordinator>.Instance);

        await coordinator.StartAsync(CancellationToken.None);
        Assert.False(drainState.IsDraining);

        lifetime.StopApplication();
        Assert.True(drainState.IsDraining); // flips synchronously via the ApplicationStopping callback

        // Simulates what actually removes tracker entries in production - a hub's own
        // OnDisconnectedAsync, triggered by the "Reconnect" push causing the client to drop. This
        // test's own dispatcher does not simulate a real client, so it does it by hand.
        _ = Task.Run(async () =>
        {
            await Task.Delay(100);
            tracker.Remove(connectionA);
            tracker.Remove(connectionB);
        });

        await coordinator.StopAsync(CancellationToken.None);

        Assert.Equal(2, dispatcher.Dispatches.Count);
        Assert.All(dispatcher.Dispatches, d => Assert.Equal("Reconnect", d.Method));
        Assert.Contains(dispatcher.Dispatches, d => d.ConnectionId == connectionA);
        Assert.Contains(dispatcher.Dispatches, d => d.ConnectionId == connectionB);

        var remaining = await registry.GetConnectionsAsync(principal, CancellationToken.None);
        Assert.Empty(remaining);
    }

    [Fact]
    public async Task StopAsync_WithNoConnections_CompletesImmediately_WithoutWaitingForTheFullTimeout()
    {
        var nodeId = new NodeId($"node-{Guid.NewGuid():N}");
        var registry = new RedisConnectionRegistry(
            fixture.Multiplexer, Options.Create(new ConnectionRegistryOptions()), NullLogger<RedisConnectionRegistry>.Instance);
        var coordinator = new ConnectionDrainCoordinator(
            new LocalConnectionTracker(), new FakeLocalConnectionDispatcher(), registry, nodeId,
            new FakeHostApplicationLifetime(), new DrainState(),
            Options.Create(new DrainOptions { DrainTimeout = TimeSpan.FromSeconds(20) }),
            NullLogger<ConnectionDrainCoordinator>.Instance);
        await coordinator.StartAsync(CancellationToken.None);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await coordinator.StopAsync(cts.Token);

        Assert.False(cts.IsCancellationRequested, "Should have returned well before the 20s DrainTimeout.");
    }

    private sealed class FakeHostApplicationLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource _stopping = new();

        public CancellationToken ApplicationStarted => CancellationToken.None;

        public CancellationToken ApplicationStopping => _stopping.Token;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication() => _stopping.Cancel();
    }

    private sealed class FakeLocalConnectionDispatcher : ILocalConnectionDispatcher
    {
        public ConcurrentBag<(ConnectionId ConnectionId, string Method, string PayloadJson)> Dispatches { get; } = [];

        public Task<DispatchOutcome> DispatchAsync(ConnectionId connectionId, string method, string payloadJson, CancellationToken cancellationToken)
        {
            Dispatches.Add((connectionId, method, payloadJson));
            return Task.FromResult(DispatchOutcome.Delivered);
        }
    }
}
