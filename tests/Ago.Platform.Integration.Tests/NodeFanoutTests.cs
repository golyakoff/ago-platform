using System.Collections.Concurrent;
using Ago.Platform.Abstractions;
using Ago.Platform.Messaging.RabbitMq;
using Ago.Platform.Realtime;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Ago.Platform.Integration.Tests;

/// <summary>
/// 3-02's backlog item, at the platform level: the concrete proof behind Stage 3's "three Api
/// replicas serve one conversation correctly." A connection on "node A" and a connection on "node B"
/// each register under a different <see cref="PrincipalKey"/>; publishing one fan-out to both
/// principals must reach each node's own <see cref="ILocalConnectionDispatcher"/> and no other -
/// real Redis, real RabbitMQ (Testcontainers), a hand-written fake for the one port this level
/// deliberately does not go further than (testing.md: no mocking framework for ports we own).
/// </summary>
[Collection(NodeFanoutCollection.Name)]
public sealed class NodeFanoutTests(NodeFanoutFixture fixture)
{
    [Fact]
    public async Task PublishToTwoPrincipalsOnDifferentNodes_EachNodesDispatcherReceivesOnlyItsOwn()
    {
        var registry = CreateRegistry();
        var nodeA = new NodeId($"node-a-{Guid.NewGuid():N}");
        var nodeB = new NodeId($"node-b-{Guid.NewGuid():N}");
        var visitor = new PrincipalKey($"visitor:{Guid.NewGuid()}");
        var operatorKey = new PrincipalKey($"operator:{Guid.NewGuid()}");
        var visitorConnection = new ConnectionId(Guid.NewGuid().ToString());
        var operatorConnection = new ConnectionId(Guid.NewGuid().ToString());

        await registry.RegisterAsync(visitorConnection, nodeA, visitor, CancellationToken.None);
        await registry.RegisterAsync(operatorConnection, nodeB, operatorKey, CancellationToken.None);

        var dispatcherA = new FakeLocalConnectionDispatcher();
        var dispatcherB = new FakeLocalConnectionDispatcher();
        await using var consumerConnectionA = new RabbitMqConnection(fixture.CreateRabbitMqOptions());
        await using var consumerConnectionB = new RabbitMqConnection(fixture.CreateRabbitMqOptions());
        var consumerA = new NodeDeliveryConsumer(new RabbitMqEventConsumer(consumerConnectionA), dispatcherA, nodeA, NullLogger<NodeDeliveryConsumer>.Instance);
        var consumerB = new NodeDeliveryConsumer(new RabbitMqEventConsumer(consumerConnectionB), dispatcherB, nodeB, NullLogger<NodeDeliveryConsumer>.Instance);
        await consumerA.StartAsync(CancellationToken.None);
        await consumerB.StartAsync(CancellationToken.None);
        // BackgroundService.StartAsync returns once ExecuteAsync yields at its first await, not
        // once SubscribeAsync has actually finished declaring the exchange/queue/binding - the
        // same RabbitMQ subscription-timing gap OutboxDispatcherTests documents for LISTEN/NOTIFY.
        await Task.Delay(TimeSpan.FromMilliseconds(500));

        try
        {
            await using var publisherConnection = new RabbitMqConnection(fixture.CreateRabbitMqOptions());
            var fanout = new NodeFanoutPublisher(registry, new RabbitMqEventPublisher(publisherConnection), new FakeClock(DateTimeOffset.UtcNow));

            await fanout.PublishAsync(
                [visitor, operatorKey], "MessageReceived", "{\"body\":\"hi\"}", Guid.NewGuid(), CancellationToken.None);

            var delivered = await RabbitMqTestHelpers.WaitUntilAsync(
                () => dispatcherA.Dispatches.Count > 0 && dispatcherB.Dispatches.Count > 0, TimeSpan.FromSeconds(10));
            Assert.True(delivered, "Timed out waiting for both nodes' dispatchers to receive their delivery.");
        }
        finally
        {
            await consumerA.StopAsync(CancellationToken.None);
            await consumerB.StopAsync(CancellationToken.None);
        }

        var receivedByA = Assert.Single(dispatcherA.Dispatches);
        Assert.Equal(visitorConnection, receivedByA.ConnectionId);
        var receivedByB = Assert.Single(dispatcherB.Dispatches);
        Assert.Equal(operatorConnection, receivedByB.ConnectionId);
    }

    [Fact]
    public async Task PublishToAPrincipalWithNoRegisteredConnections_PublishesNothing_DoesNotThrow()
    {
        var registry = CreateRegistry();
        await using var publisherConnection = new RabbitMqConnection(fixture.CreateRabbitMqOptions());
        var fanout = new NodeFanoutPublisher(registry, new RabbitMqEventPublisher(publisherConnection), new FakeClock(DateTimeOffset.UtcNow));

        var principalWithNoConnections = new PrincipalKey($"visitor:{Guid.NewGuid()}");

        var exception = await Record.ExceptionAsync(() =>
            fanout.PublishAsync([principalWithNoConnections], "MessageReceived", "{}", Guid.NewGuid(), CancellationToken.None));

        Assert.Null(exception); // realtime.md: a stale/absent registry entry is harmless, never an error
    }

    private RedisConnectionRegistry CreateRegistry() =>
        new(fixture.RedisMultiplexer, Options.Create(new ConnectionRegistryOptions()), NullLogger<RedisConnectionRegistry>.Instance);

    private sealed class FakeLocalConnectionDispatcher : ILocalConnectionDispatcher
    {
        public ConcurrentBag<(ConnectionId ConnectionId, string Method, string PayloadJson)> Dispatches { get; } = [];

        public Task DispatchAsync(ConnectionId connectionId, string method, string payloadJson, CancellationToken cancellationToken)
        {
            Dispatches.Add((connectionId, method, payloadJson));
            return Task.CompletedTask;
        }
    }
}
