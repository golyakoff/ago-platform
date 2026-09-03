using System.Collections.Concurrent;
using Ago.Platform.Abstractions;
using Ago.Platform.Messaging.RabbitMq;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ago.Platform.Integration.Tests;

/// <summary>
/// `15-15`: the trap this item names explicitly is making every `Competing` queue auto-delete, which
/// would silently drop messages published while a genuinely durable consumer (the overwhelming
/// majority - `messaging.md`'s whole topic table) has nobody attached. These two tests are the proof
/// that did not happen: one queue that is gone the instant its declaring connection closes
/// (<see cref="QueueLifetime.ProcessScoped"/>, node-delivery's own shape), and one that is not
/// (<see cref="QueueLifetime.Durable"/>, the default every existing subscription keeps using
/// unmodified) - with a message published into the gap still there when a consumer reattaches.
/// </summary>
[Collection(RabbitMqCollection.Name)]
public sealed class RabbitMqQueueLifetimeTests(RabbitMqFixture fixture)
{
    [Fact]
    public async Task ProcessScoped_QueueAndItsRetryQueueAreGoneOnceItsConnectionCloses_DeadLetterQueueIsNotBecauseItIsNotSubscriptionOwned()
    {
        var topic = RabbitMqTestHelpers.NewTopic();
        const string consumerName = "pod-abc123";
        var deadLetterName = $"dlq.{Guid.NewGuid():N}";
        var retryPolicy = new RetryPolicy(MaxAttempts: 1, InitialBackoff: TimeSpan.Zero, DeadLetterName: deadLetterName);

        var declaringConnection = new RabbitMqConnection(fixture.CreateOptions(), NullLogger<RabbitMqConnection>.Instance);
        var consumer = new RabbitMqEventConsumer(declaringConnection);
        await consumer.SubscribeAsync(
            topic, SubscriptionMode.Competing, consumerName, retryPolicy, QueueLifetime.ProcessScoped,
            (_, ctx, ct) => ctx.AckAsync(ct), CancellationToken.None);

        // A checker connection independent of the one that declared the queues - the point being
        // proven is that the *declaring* connection's own death is what deletes them, not that
        // checking for them happens to close something.
        await using var checker = new RabbitMqConnection(fixture.CreateOptions(), NullLogger<RabbitMqConnection>.Instance);
        var queueName = $"{topic}.{consumerName}";
        var retryQueueName = $"{queueName}.retry";

        Assert.True(await RabbitMqTestHelpers.QueueExistsAsync(checker, queueName), "Main queue should exist right after SubscribeAsync.");
        Assert.True(await RabbitMqTestHelpers.QueueExistsAsync(checker, retryQueueName), "Retry queue should exist right after SubscribeAsync.");
        Assert.True(await RabbitMqTestHelpers.QueueExistsAsync(checker, deadLetterName), "Dead-letter queue should exist right after SubscribeAsync.");

        // The one action this whole item is about: the process this queue belonged to is gone.
        await declaringConnection.DisposeAsync();

        Assert.True(
            await RabbitMqTestHelpers.WaitUntilAsync(async () => !await RabbitMqTestHelpers.QueueExistsAsync(checker, queueName), TimeSpan.FromSeconds(10)),
            "Main queue should be gone once its declaring connection closed.");
        Assert.True(
            await RabbitMqTestHelpers.WaitUntilAsync(async () => !await RabbitMqTestHelpers.QueueExistsAsync(checker, retryQueueName), TimeSpan.FromSeconds(10)),
            "Retry queue should be gone once its declaring connection closed.");

        // Deliberately the opposite assertion from the two above: the dead-letter queue is never
        // subscription-owned (RabbitMqEventConsumer's own remarks - a DLQ can legitimately be shared
        // across independent subscriptions, proven by the pre-existing
        // Broadcast_TwoConsumers_BothReceiveEveryMessage), so QueueLifetime never touches it. A
        // ProcessScoped subscription's DLQ surviving its connection is correct, not a leak.
        Assert.True(
            await RabbitMqTestHelpers.QueueExistsAsync(checker, deadLetterName),
            "Dead-letter queue is not subscription-owned and must still exist after the declaring connection closed.");
    }

    /// <summary>
    /// The half that is easy to skip, and the one the brief this item shipped from called out by name:
    /// proving durability held rather than only proving ephemerality worked. A message published while
    /// the only consumer of a Durable subscription is disconnected must still be there - intact - when
    /// a new consumer of the same logical subscription (same topic, same consumerName) attaches.
    /// </summary>
    [Fact]
    public async Task Durable_QueueAndItsMessageSurviveItsOnlyConsumerDisconnecting()
    {
        var topic = RabbitMqTestHelpers.NewTopic();
        const string consumerName = "durable-consumer";
        var retryPolicy = new RetryPolicy(MaxAttempts: 3, InitialBackoff: TimeSpan.FromMilliseconds(200), DeadLetterName: $"dlq.{Guid.NewGuid():N}");
        var queueName = $"{topic}.{consumerName}";

        var firstConnection = new RabbitMqConnection(fixture.CreateOptions(), NullLogger<RabbitMqConnection>.Instance);
        var firstConsumer = new RabbitMqEventConsumer(firstConnection);
        // The six-argument overload, deliberately - every existing durable subscription in this system
        // (messaging.md's whole topic table) calls exactly this one and gets exactly this behaviour
        // without being told about QueueLifetime at all.
        await firstConsumer.SubscribeAsync(
            topic, SubscriptionMode.Competing, consumerName, retryPolicy, (_, ctx, ct) => ctx.AckAsync(ct), CancellationToken.None);

        await using var checker = new RabbitMqConnection(fixture.CreateOptions(), NullLogger<RabbitMqConnection>.Instance);
        Assert.True(await RabbitMqTestHelpers.QueueExistsAsync(checker, queueName), "Durable queue should exist right after SubscribeAsync.");

        // Drop the only consumer - the exact moment `15-15`'s trap would have deleted this queue had
        // Competing simply become auto-delete across the board.
        await firstConnection.DisposeAsync();

        Assert.True(
            await RabbitMqTestHelpers.QueueExistsAsync(checker, queueName),
            "Durable queue must still exist after its only consumer disconnected - this is the guarantee 15-15 must not break.");

        // Published into the gap: nobody is attached to this queue right now.
        await using var publisherConnection = new RabbitMqConnection(fixture.CreateOptions(), NullLogger<RabbitMqConnection>.Instance);
        var publisher = new RabbitMqEventPublisher(publisherConnection, NullLogger<RabbitMqEventPublisher>.Instance);
        var sent = new EventEnvelope(Guid.NewGuid(), topic, 1, "key-1", DateTimeOffset.UtcNow, Guid.NewGuid(), "{\"intact\":true}");
        await publisher.PublishAsync(sent, CancellationToken.None);

        Assert.True(await RabbitMqTestHelpers.QueueExistsAsync(checker, queueName), "Durable queue must still exist with a message waiting in it.");

        // Reattach: same topic, same consumerName - a new replica of the same logical durable
        // subscription, exactly what messaging.md calls Competing's actual purpose.
        await using var secondConnection = new RabbitMqConnection(fixture.CreateOptions(), NullLogger<RabbitMqConnection>.Instance);
        var secondConsumer = new RabbitMqEventConsumer(secondConnection);
        var received = new ConcurrentBag<EventEnvelope>();
        await secondConsumer.SubscribeAsync(topic, SubscriptionMode.Competing, consumerName, retryPolicy, (envelope, ctx, ct) =>
        {
            received.Add(envelope);
            return ctx.AckAsync(ct);
        }, CancellationToken.None);

        await RabbitMqTestHelpers.WaitUntilAsync(() => !received.IsEmpty, TimeSpan.FromSeconds(10));

        var envelope = Assert.Single(received);
        Assert.Equal(sent.MessageId, envelope.MessageId);
        Assert.Equal(sent.Type, envelope.Type);
        Assert.Equal(sent.PartitionKey, envelope.PartitionKey);
        Assert.Equal(sent.CorrelationId, envelope.CorrelationId);
        Assert.Equal(sent.Payload, envelope.Payload);
    }
}
