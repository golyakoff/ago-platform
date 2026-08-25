using System.Collections.Concurrent;
using System.Diagnostics;
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

    /// <summary>
    /// `7-08`: the fan-out's span attributes, against the real registry - the seam that matters,
    /// because the numbers are only worth anything if they are what Redis actually answered with.
    /// The three are deliberately made to differ: two recipients, three connections (one of them has
    /// two tabs open), two nodes. An implementation that reported the recipient count where it meant
    /// the connection count - the easiest mistake to make here, and invisible in any test where a
    /// recipient has exactly one connection - reports 2 for a value that is 3.
    /// </summary>
    [Fact]
    public async Task AFanoutsSpan_CarriesRecipientsConnectionsAndNodes_AsResolvedFromTheRegistry()
    {
        var registry = CreateRegistry();
        var nodeA = new NodeId($"node-a-{Guid.NewGuid():N}");
        var nodeB = new NodeId($"node-b-{Guid.NewGuid():N}");
        var visitor = new PrincipalKey($"visitor:{Guid.NewGuid()}");
        var operatorKey = new PrincipalKey($"operator:{Guid.NewGuid()}");

        // The visitor has the same conversation open in two tabs; the operator has one console.
        await registry.RegisterAsync(new ConnectionId(Guid.NewGuid().ToString()), nodeA, visitor, CancellationToken.None);
        await registry.RegisterAsync(new ConnectionId(Guid.NewGuid().ToString()), nodeA, visitor, CancellationToken.None);
        await registry.RegisterAsync(new ConnectionId(Guid.NewGuid().ToString()), nodeB, operatorKey, CancellationToken.None);

        await using var publisherConnection = new RabbitMqConnection(fixture.CreateRabbitMqOptions());
        var fanout = new NodeFanoutPublisher(registry, new RabbitMqEventPublisher(publisherConnection), new FakeClock(DateTimeOffset.UtcNow));

        // Stands in for the "{topic} process" span the consumer that calls into the fan-out is
        // already inside (`7-01`) - the span this hop enriches rather than starting one of its own.
        using var source = new ActivitySource($"test-{Guid.NewGuid():N}");
        using var listener = new ActivityListener
        {
            ShouldListenTo = candidate => candidate == source,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listener);

        FanoutResult result;
        using (var callerSpan = source.StartActivity("fanout-caller"))
        {
            Assert.NotNull(callerSpan);
            result = await fanout.PublishAsync(
                [visitor, operatorKey], "MessageReceived", "{\"body\":\"hi\"}", Guid.NewGuid(), CancellationToken.None);

            Assert.Equal(2, callerSpan.GetTagItem("ago.fanout.recipients"));
            Assert.Equal(3, callerSpan.GetTagItem("ago.fanout.connections"));
            Assert.Equal(2, callerSpan.GetTagItem("ago.fanout.nodes"));
        }

        // The same facts, returned to the caller so a product can dimension them in its own
        // vocabulary (FanoutResult's own remarks on why the platform does not tag them itself).
        Assert.Equal(3, result.TotalConnections);
        Assert.Equal(2, result.Recipients.Single(r => r.Recipient == visitor).Connections);
        Assert.Equal(1, result.Recipients.Single(r => r.Recipient == operatorKey).Connections);
    }

    /// <summary>
    /// The ordinary zero: a visitor who closed the tab. It must be reported as zero rather than
    /// omitted, because "resolved, and had nobody" is the fact a caller needs in order to tell it
    /// apart from "was never a recipient at all".
    /// </summary>
    [Fact]
    public async Task ARecipientWithNoConnections_IsReportedAsResolvedWithZero_NotDroppedFromTheResult()
    {
        var registry = CreateRegistry();
        await using var publisherConnection = new RabbitMqConnection(fixture.CreateRabbitMqOptions());
        var fanout = new NodeFanoutPublisher(registry, new RabbitMqEventPublisher(publisherConnection), new FakeClock(DateTimeOffset.UtcNow));
        var absent = new PrincipalKey($"visitor:{Guid.NewGuid()}");

        var result = await fanout.PublishAsync([absent], "MessageReceived", "{}", Guid.NewGuid(), CancellationToken.None);

        var recipient = Assert.Single(result.Recipients);
        Assert.Equal(absent, recipient.Recipient);
        Assert.Equal(0, recipient.Connections);
        Assert.Equal(0, result.TotalConnections);
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

        public Task<DispatchOutcome> DispatchAsync(ConnectionId connectionId, string method, string payloadJson, CancellationToken cancellationToken)
        {
            Dispatches.Add((connectionId, method, payloadJson));
            return Task.FromResult(DispatchOutcome.Delivered);
        }
    }
}
