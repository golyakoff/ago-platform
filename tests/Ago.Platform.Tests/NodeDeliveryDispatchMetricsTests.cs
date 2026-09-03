using System.Collections.Concurrent;
using System.Text.Json;
using Ago.Platform.Abstractions;
using Ago.Platform.Realtime;
using Microsoft.Extensions.Logging.Abstractions;
using OpenTelemetry;
using OpenTelemetry.Metrics;

namespace Ago.Platform.Tests;

/// <summary>
/// `7-08`: the number the fan-out path never had - how many of the deliveries a node was handed
/// actually met a connection it still holds.
///
/// Unit level, not <c>Ago.Platform.Integration.Tests</c>, and deliberately so: unlike `7-07`'s gauge,
/// whose defect only existed in the seam between the real registry and the real heartbeat, every way
/// this counter can be wrong is reachable with a fake <see cref="IEventConsumer"/> that hands
/// <see cref="NodeDeliveryConsumer"/> an envelope directly - counting the wrong event, attributing
/// the wrong outcome, counting twice on the exception path, or counting the drain's own pushes. A
/// broker in the middle would slow that down without testing any of it. The real-broker path is
/// already covered by <c>NodeFanoutTests</c>.
/// </summary>
public sealed class NodeDeliveryDispatchMetricsTests
{
    /// <summary>
    /// The one that matters: a stale registry entry and a live connection are indistinguishable to
    /// the consumer, which acknowledges both and always has. An implementation that counted "a
    /// dispatch happened" rather than "what the dispatcher reported" would put both of these under
    /// `delivered` and look exactly as healthy as a node that reached everybody.
    /// </summary>
    [Fact]
    public async Task AConnectionTheNodeNoLongerHolds_IsCountedApartFromOneItDoes()
    {
        var node = new NodeId($"node-{Guid.NewGuid():N}");
        var live = new ConnectionId(Guid.NewGuid().ToString());
        var gone = new ConnectionId(Guid.NewGuid().ToString());
        var dispatcher = new OutcomeScriptedDispatcher
        {
            Outcomes = { [live] = DispatchOutcome.Delivered, [gone] = DispatchOutcome.ConnectionNotLocal },
        };

        using var reader = new DispatchCounterReader(node);
        await DeliverAsync(node, dispatcher, live, gone);

        Assert.Equal(1, reader.Read(RealtimeMetrics.DeliveredOutcome));
        Assert.Equal(1, reader.Read(RealtimeMetrics.ConnectionNotLocalOutcome));
        Assert.Equal(0, reader.Read(RealtimeMetrics.FailedOutcome));
    }

    /// <summary>
    /// A dispatcher that throws is a third thing, not a fourth spelling of "gone" - and the throw
    /// must produce exactly one point, not one on the way in and another in the catch.
    /// </summary>
    [Fact]
    public async Task ADispatcherThatThrows_IsCountedOnceAsFailed_AndTheRestOfTheBatchStillCounts()
    {
        var node = new NodeId($"node-{Guid.NewGuid():N}");
        var throwing = new ConnectionId(Guid.NewGuid().ToString());
        var live = new ConnectionId(Guid.NewGuid().ToString());
        var dispatcher = new OutcomeScriptedDispatcher
        {
            Throwing = { throwing },
            Outcomes = { [live] = DispatchOutcome.Delivered },
        };

        using var reader = new DispatchCounterReader(node);
        await DeliverAsync(node, dispatcher, throwing, live);

        Assert.Equal(1, reader.Read(RealtimeMetrics.FailedOutcome));
        Assert.Equal(1, reader.Read(RealtimeMetrics.DeliveredOutcome));
        Assert.Equal(0, reader.Read(RealtimeMetrics.ConnectionNotLocalOutcome));
    }

    /// <summary>
    /// The `7-07` check, applied before shipping rather than after: a redelivery is a second real
    /// attempt this node made, so it counts again - and that is stated, not accidental. What must
    /// *not* happen is one attempt producing two points, which is what a counter incremented both
    /// beside the call and in a catch block would do.
    /// </summary>
    [Fact]
    public async Task ARedeliveredNodeDelivery_CountsOncePerAttempt_NotTwicePerAttempt()
    {
        var node = new NodeId($"node-{Guid.NewGuid():N}");
        var connection = new ConnectionId(Guid.NewGuid().ToString());
        var dispatcher = new OutcomeScriptedDispatcher
        {
            Outcomes = { [connection] = DispatchOutcome.Delivered },
        };

        using var reader = new DispatchCounterReader(node);
        await DeliverAsync(node, dispatcher, connection);
        Assert.Equal(1, reader.Read(RealtimeMetrics.DeliveredOutcome));

        await DeliverAsync(node, dispatcher, connection); // the broker redelivering the same envelope
        Assert.Equal(2, reader.Read(RealtimeMetrics.DeliveredOutcome));
        Assert.Equal(2, dispatcher.Calls.Count); // two attempts, two points - never one attempt, two points
    }

    /// <summary>
    /// `ConnectionDrainCoordinator` is <see cref="ILocalConnectionDispatcher"/>'s other caller, and a
    /// rolling deploy pushes one "Reconnect" per connection through it. If the counter lived beside
    /// the port instead of inside the fan-out consumer, every deploy would show up as a burst of
    /// message deliveries - the same "an instrument counts a second, unrelated mechanism" shape
    /// `7-07` found in the connections gauge, checked here before it can happen rather than after.
    /// </summary>
    [Fact]
    public async Task ADrainsReconnectPushes_DoNotTouchTheDispatchCounter()
    {
        var node = new NodeId($"node-{Guid.NewGuid():N}");
        var connection = new ConnectionId(Guid.NewGuid().ToString());
        var dispatcher = new OutcomeScriptedDispatcher
        {
            Outcomes = { [connection] = DispatchOutcome.Delivered },
        };

        using var reader = new DispatchCounterReader(node);
        await dispatcher.DispatchAsync(connection, "Reconnect", "{\"after\":\"00:00:01\"}", CancellationToken.None);

        Assert.Equal(0, reader.Read(RealtimeMetrics.DeliveredOutcome));
        Assert.Equal(0, reader.Read(RealtimeMetrics.ConnectionNotLocalOutcome));
        Assert.Equal(0, reader.Read(RealtimeMetrics.FailedOutcome));
    }

    private static async Task DeliverAsync(
        NodeId node, ILocalConnectionDispatcher dispatcher, params ConnectionId[] connections)
    {
        var consumer = new HandlerCapturingEventConsumer();
        var delivery = new NodeDeliveryConsumer(consumer, dispatcher, node, NullLogger<NodeDeliveryConsumer>.Instance);

        await delivery.StartAsync(CancellationToken.None);
        try
        {
            var envelope = new EventEnvelope(
                MessageId: Guid.NewGuid(),
                Type: NodeTopics.For(node),
                Version: 1,
                PartitionKey: node.Value,
                OccurredAt: DateTimeOffset.UtcNow,
                CorrelationId: Guid.NewGuid(),
                Payload: JsonSerializer.Serialize(new NodeDelivery(connections, "MessageReceived", "{}")));

            var handler = await consumer.Subscribed;
            await handler(envelope, new AckRecordingContext(), CancellationToken.None);
        }
        finally
        {
            await delivery.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>Reads one node's points off the real counter through the OTel SDK's in-memory
    /// reader - tagged by node id, which is also what keeps two of these tests running in parallel
    /// against the same process-wide <c>Meter</c> from seeing each other's points.</summary>
    private sealed class DispatchCounterReader : IDisposable
    {
        private readonly List<Metric> _exported = [];
        private readonly MeterProvider _provider;
        private readonly NodeId _node;

        // Built eagerly, in the constructor: the SDK only collects measurements made while a
        // MeterProvider is listening, so building it lazily on the first Read would silently report
        // zero for everything that happened before then - a test that passes for the wrong reason.
        public DispatchCounterReader(NodeId node)
        {
            _node = node;
            _provider = Sdk.CreateMeterProviderBuilder()
                .AddMeter(RealtimeMetrics.MeterName)
                .AddInMemoryExporter(_exported)
                .Build();
        }

        public long Read(string outcome)
        {
            _exported.Clear();
            _provider.ForceFlush();
            var counter = _exported.SingleOrDefault(m => m.Name == RealtimeMetrics.DispatchesInstrumentName);
            if (counter is null)
            {
                return 0; // nothing recorded yet - an absent series is a zero, not a failure
            }

            var total = 0L;
            foreach (ref readonly var point in counter.GetMetricPoints())
            {
                var matchesNode = false;
                var matchesOutcome = false;
                foreach (var tag in point.Tags)
                {
                    matchesNode |= tag.Key == "node" && (string?)tag.Value == _node.Value;
                    matchesOutcome |= tag.Key == "outcome" && (string?)tag.Value == outcome;
                }

                if (matchesNode && matchesOutcome)
                {
                    total += point.GetSumLong();
                }
            }

            return total;
        }

        public void Dispose() => _provider.Dispose();
    }

    private sealed class OutcomeScriptedDispatcher : ILocalConnectionDispatcher
    {
        public Dictionary<ConnectionId, DispatchOutcome> Outcomes { get; } = [];

        public HashSet<ConnectionId> Throwing { get; } = [];

        public ConcurrentBag<ConnectionId> Calls { get; } = [];

        public Task<DispatchOutcome> DispatchAsync(
            ConnectionId connectionId, string method, string payloadJson, CancellationToken cancellationToken)
        {
            Calls.Add(connectionId);
            if (Throwing.Contains(connectionId))
            {
                throw new InvalidOperationException("transport is gone");
            }

            return Task.FromResult(Outcomes[connectionId]);
        }
    }

    /// <summary>Hands the test the handler <see cref="NodeDeliveryConsumer"/> subscribed with, so an
    /// envelope can be delivered to it without a broker in the way.</summary>
    private sealed class HandlerCapturingEventConsumer : IEventConsumer
    {
        private readonly TaskCompletionSource<Func<EventEnvelope, IMessageContext, CancellationToken, Task>> _subscribed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<Func<EventEnvelope, IMessageContext, CancellationToken, Task>> Subscribed => _subscribed.Task;

        public Task SubscribeAsync(
            string topic,
            SubscriptionMode mode,
            string consumerName,
            RetryPolicy retryPolicy,
            Func<EventEnvelope, IMessageContext, CancellationToken, Task> handler,
            CancellationToken cancellationToken) =>
            SubscribeAsync(topic, mode, consumerName, retryPolicy, QueueLifetime.Durable, handler, cancellationToken);

        // `15-15`: NodeDeliveryConsumer now calls this overload directly (QueueLifetime.ProcessScoped),
        // so this is the one that actually captures the handler under test; the six-argument overload
        // above only exists to keep this fake satisfying the full interface.
        public Task SubscribeAsync(
            string topic,
            SubscriptionMode mode,
            string consumerName,
            RetryPolicy retryPolicy,
            QueueLifetime queueLifetime,
            Func<EventEnvelope, IMessageContext, CancellationToken, Task> handler,
            CancellationToken cancellationToken)
        {
            _subscribed.TrySetResult(handler);
            return Task.Delay(Timeout.Infinite, cancellationToken);
        }
    }

    private sealed class AckRecordingContext : IMessageContext
    {
        public Task AckAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task NackAsync(bool requeue, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task DeadLetterAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
