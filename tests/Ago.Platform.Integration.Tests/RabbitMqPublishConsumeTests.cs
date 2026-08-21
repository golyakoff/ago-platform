using System.Collections.Concurrent;
using Ago.Platform.Abstractions;
using Ago.Platform.Messaging.RabbitMq;

namespace Ago.Platform.Integration.Tests;

[Collection(RabbitMqCollection.Name)]
public sealed class RabbitMqPublishConsumeTests(RabbitMqFixture fixture)
{
    private static readonly RetryPolicy RetryPolicy = new(MaxAttempts: 3, InitialBackoff: TimeSpan.FromMilliseconds(200), DeadLetterName: $"dlq.{Guid.NewGuid():N}");

    [Fact]
    public async Task PublishThenConsume_Competing_DeliversTheEnvelopeIntact()
    {
        var topic = RabbitMqTestHelpers.NewTopic();
        await using var connection = new RabbitMqConnection(fixture.CreateOptions());
        var publisher = new RabbitMqEventPublisher(connection);
        var consumer = new RabbitMqEventConsumer(connection);

        var received = new ConcurrentBag<EventEnvelope>();
        await consumer.SubscribeAsync(topic, SubscriptionMode.Competing, RetryPolicy, (envelope, ctx, ct) =>
        {
            received.Add(envelope);
            return ctx.AckAsync(ct);
        }, CancellationToken.None);

        var sent = new EventEnvelope(Guid.NewGuid(), topic, 1, "key-1", DateTimeOffset.UtcNow, Guid.NewGuid(), "{\"hello\":true}");
        await publisher.PublishAsync(sent, CancellationToken.None);

        await RabbitMqTestHelpers.WaitUntilAsync(() => !received.IsEmpty, TimeSpan.FromSeconds(10));

        var envelope = Assert.Single(received);
        Assert.Equal(sent.MessageId, envelope.MessageId);
        Assert.Equal(sent.Type, envelope.Type);
        Assert.Equal(sent.PartitionKey, envelope.PartitionKey);
        Assert.Equal(sent.CorrelationId, envelope.CorrelationId);
        Assert.Equal(sent.Payload, envelope.Payload);
    }

    [Fact]
    public async Task Competing_TwoConsumers_EachMessageGoesToExactlyOne()
    {
        var topic = RabbitMqTestHelpers.NewTopic();
        await using var connection = new RabbitMqConnection(fixture.CreateOptions());
        var publisher = new RabbitMqEventPublisher(connection);
        var consumer = new RabbitMqEventConsumer(connection);

        var receivedByA = new ConcurrentBag<Guid>();
        var receivedByB = new ConcurrentBag<Guid>();
        await consumer.SubscribeAsync(topic, SubscriptionMode.Competing, RetryPolicy, (envelope, ctx, ct) =>
        {
            receivedByA.Add(envelope.MessageId);
            return ctx.AckAsync(ct);
        }, CancellationToken.None);
        await consumer.SubscribeAsync(topic, SubscriptionMode.Competing, RetryPolicy, (envelope, ctx, ct) =>
        {
            receivedByB.Add(envelope.MessageId);
            return ctx.AckAsync(ct);
        }, CancellationToken.None);

        const int count = 10;
        var sentIds = new List<Guid>();
        for (var i = 0; i < count; i++)
        {
            var envelope = new EventEnvelope(Guid.NewGuid(), topic, 1, $"key-{i}", DateTimeOffset.UtcNow, Guid.NewGuid(), "{}");
            sentIds.Add(envelope.MessageId);
            await publisher.PublishAsync(envelope, CancellationToken.None);
        }

        await RabbitMqTestHelpers.WaitUntilAsync(() => receivedByA.Count + receivedByB.Count >= count, TimeSpan.FromSeconds(10));

        var allReceived = receivedByA.Concat(receivedByB).ToList();
        Assert.Equal(count, allReceived.Count);
        Assert.Equal(count, allReceived.Distinct().Count()); // no message delivered to both
        Assert.Equal(sentIds.OrderBy(x => x), allReceived.OrderBy(x => x));
    }

    [Fact]
    public async Task Broadcast_TwoConsumers_BothReceiveEveryMessage()
    {
        var topic = RabbitMqTestHelpers.NewTopic();
        await using var connection = new RabbitMqConnection(fixture.CreateOptions());
        var publisher = new RabbitMqEventPublisher(connection);
        var consumer = new RabbitMqEventConsumer(connection);

        var receivedByA = new ConcurrentBag<Guid>();
        var receivedByB = new ConcurrentBag<Guid>();
        await consumer.SubscribeAsync(topic, SubscriptionMode.Broadcast, RetryPolicy, (envelope, ctx, ct) =>
        {
            receivedByA.Add(envelope.MessageId);
            return ctx.AckAsync(ct);
        }, CancellationToken.None);
        await consumer.SubscribeAsync(topic, SubscriptionMode.Broadcast, RetryPolicy, (envelope, ctx, ct) =>
        {
            receivedByB.Add(envelope.MessageId);
            return ctx.AckAsync(ct);
        }, CancellationToken.None);

        const int count = 5;
        var sentIds = new List<Guid>();
        for (var i = 0; i < count; i++)
        {
            var envelope = new EventEnvelope(Guid.NewGuid(), topic, 1, $"key-{i}", DateTimeOffset.UtcNow, Guid.NewGuid(), "{}");
            sentIds.Add(envelope.MessageId);
            await publisher.PublishAsync(envelope, CancellationToken.None);
        }

        await RabbitMqTestHelpers.WaitUntilAsync(() => receivedByA.Count >= count && receivedByB.Count >= count, TimeSpan.FromSeconds(10));

        Assert.Equal(sentIds.OrderBy(x => x), receivedByA.Distinct().OrderBy(x => x));
        Assert.Equal(sentIds.OrderBy(x => x), receivedByB.Distinct().OrderBy(x => x));
    }
}
