using System.Collections.Concurrent;
using System.Text;
using Ago.Platform.Abstractions;
using Ago.Platform.Messaging.RabbitMq;
using RabbitMQ.Client;

namespace Ago.Platform.Integration.Tests;

/// <summary>messaging.md: "N attempts with exponential backoff, then dead-letter with the full
/// envelope and the last exception." Backoff itself is not timed precisely here (that is Stage 7's
/// job) - these tests prove the *mechanism*: a requeue is redelivered, and exhausting attempts lands
/// on the configured dead-letter queue with the message intact.</summary>
[Collection(RabbitMqCollection.Name)]
public sealed class RabbitMqRetryAndDeadLetterTests(RabbitMqFixture fixture)
{
    [Fact]
    public async Task NackWithRequeue_RedeliversTheMessage()
    {
        var topic = RabbitMqTestHelpers.NewTopic();
        var retryPolicy = new RetryPolicy(MaxAttempts: 5, InitialBackoff: TimeSpan.FromMilliseconds(100), DeadLetterName: $"dlq.{Guid.NewGuid():N}");
        await using var connection = new RabbitMqConnection(fixture.CreateOptions());
        var publisher = new RabbitMqEventPublisher(connection);
        var consumer = new RabbitMqEventConsumer(connection);

        var attempts = new ConcurrentBag<Guid>();
        await consumer.SubscribeAsync(topic, SubscriptionMode.Competing, "retry-consumer", retryPolicy, async (envelope, ctx, ct) =>
        {
            attempts.Add(envelope.MessageId);
            if (attempts.Count(id => id == envelope.MessageId) == 1)
            {
                await ctx.NackAsync(requeue: true, ct);
                return;
            }

            await ctx.AckAsync(ct);
        }, CancellationToken.None);

        var envelope = new EventEnvelope(Guid.NewGuid(), topic, 1, "key", DateTimeOffset.UtcNow, Guid.NewGuid(), "{}");
        await publisher.PublishAsync(envelope, CancellationToken.None);

        await RabbitMqTestHelpers.WaitUntilAsync(
            () => attempts.Count(id => id == envelope.MessageId) >= 2, TimeSpan.FromSeconds(10));

        Assert.True(attempts.Count(id => id == envelope.MessageId) >= 2);
    }

    [Fact]
    public async Task ExhaustingMaxAttempts_LandsOnTheDeadLetterQueueWithTheEnvelopeIntact()
    {
        var topic = RabbitMqTestHelpers.NewTopic();
        var deadLetterName = $"dlq.{Guid.NewGuid():N}";
        var retryPolicy = new RetryPolicy(MaxAttempts: 2, InitialBackoff: TimeSpan.FromMilliseconds(100), DeadLetterName: deadLetterName);
        await using var connection = new RabbitMqConnection(fixture.CreateOptions());
        var publisher = new RabbitMqEventPublisher(connection);
        var consumer = new RabbitMqEventConsumer(connection);

        await consumer.SubscribeAsync(topic, SubscriptionMode.Competing, "poison-consumer", retryPolicy, async (_, ctx, ct) =>
        {
            // Always asks to retry - forces every delivery past MaxAttempts to dead-letter.
            await ctx.NackAsync(requeue: true, ct);
        }, CancellationToken.None);

        var envelope = new EventEnvelope(Guid.NewGuid(), topic, 1, "key", DateTimeOffset.UtcNow, Guid.NewGuid(), "{\"poison\":true}");
        await publisher.PublishAsync(envelope, CancellationToken.None);

        // Drains the dead-letter queue directly - proves the row landed there, not just that the
        // original queue emptied.
        var channel = await connection.CreateChannelAsync();
        await channel.QueueDeclareAsync(deadLetterName, durable: true, exclusive: false, autoDelete: false);

        BasicGetResult? result = null;
        await RabbitMqTestHelpers.WaitUntilAsync(
            async () =>
            {
                result = await channel.BasicGetAsync(deadLetterName, autoAck: true);
                return result is not null;
            },
            TimeSpan.FromSeconds(10));

        Assert.NotNull(result);
        Assert.Equal(envelope.MessageId.ToString(), result!.BasicProperties.MessageId);
        Assert.Equal("{\"poison\":true}", Encoding.UTF8.GetString(result.Body.Span));
    }
}
