using System.Collections.Concurrent;
using System.Text;
using Ago.Platform.Abstractions;
using Ago.Platform.Messaging.RabbitMq;
using Microsoft.Extensions.Logging.Abstractions;
using OpenTelemetry;
using OpenTelemetry.Metrics;
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
        await using var connection = new RabbitMqConnection(fixture.CreateOptions(), NullLogger<RabbitMqConnection>.Instance);
        var publisher = new RabbitMqEventPublisher(connection, NullLogger<RabbitMqEventPublisher>.Instance);
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
        await using var connection = new RabbitMqConnection(fixture.CreateOptions(), NullLogger<RabbitMqConnection>.Instance);
        var publisher = new RabbitMqEventPublisher(connection, NullLogger<RabbitMqEventPublisher>.Instance);
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

    /// <summary>
    /// `7-02`'s Done-when: proves the real value change, not just that the instrument was registered -
    /// the same in-memory-reader bar `TracingEndToEndTests` already holds `7-01`'s spans to, applied
    /// to `RabbitMqMetrics`' consumer RED triad and its DLQ counter. Reuses this file's own dead-letter
    /// scenario (a handler that always asks to retry, forcing every delivery past MaxAttempts to
    /// dead-letter) rather than a fresh one, since that scenario already exercises both an error
    /// outcome and a dead-letter in one real broker round trip.
    /// </summary>
    [Fact]
    public async Task ExhaustingMaxAttempts_RecordsConsumerErrorOutcomesAndTheDeadLetterCounter()
    {
        var exportedMetrics = new List<Metric>();
        using var meterProvider = Sdk.CreateMeterProviderBuilder()
            .AddMeter(Ago.Platform.Observability.ObservabilityServiceCollectionExtensions.MeterWildcard)
            .AddInMemoryExporter(exportedMetrics)
            .Build();

        var topic = RabbitMqTestHelpers.NewTopic();
        var consumerName = $"metrics-consumer-{Guid.NewGuid():N}";
        var deadLetterName = $"dlq.{Guid.NewGuid():N}";
        var retryPolicy = new RetryPolicy(MaxAttempts: 2, InitialBackoff: TimeSpan.FromMilliseconds(100), DeadLetterName: deadLetterName);
        await using var connection = new RabbitMqConnection(fixture.CreateOptions(), NullLogger<RabbitMqConnection>.Instance);
        var publisher = new RabbitMqEventPublisher(connection, NullLogger<RabbitMqEventPublisher>.Instance);
        var consumer = new RabbitMqEventConsumer(connection);

        // Throws rather than calling ctx.NackAsync(requeue: true) directly, deliberately: the
        // consumer.count outcome tag is "did the handler complete without throwing", the same
        // exception-vs-explicit-decision distinction RabbitMqEventConsumer's own catch block already
        // draws (only an uncaught exception is treated as an error outcome there) - a handler that
        // calls NackAsync itself and returns normally is a deliberate business decision, not a
        // failure, and must not be conflated with one.
        await consumer.SubscribeAsync(topic, SubscriptionMode.Competing, consumerName, retryPolicy, (_, _, _) =>
            throw new InvalidOperationException("forced failure for the consumer RED/DLQ metrics test"),
            CancellationToken.None);

        var envelope = new EventEnvelope(Guid.NewGuid(), topic, 1, "key", DateTimeOffset.UtcNow, Guid.NewGuid(), "{\"poison\":true}");
        await publisher.PublishAsync(envelope, CancellationToken.None);

        var channel = await connection.CreateChannelAsync();
        await channel.QueueDeclareAsync(deadLetterName, durable: true, exclusive: false, autoDelete: false);
        await RabbitMqTestHelpers.WaitUntilAsync(
            async () => (await channel.BasicGetAsync(deadLetterName, autoAck: true)) is not null,
            TimeSpan.FromSeconds(10));

        meterProvider.ForceFlush();

        var count = exportedMetrics.Single(m => m.Name == "ago.platform.messaging.rabbitmq.consumer.count");
        var errorOutcomes = SumMatchingLongPoints(count, tags => tags.GetValueOrDefault("topic") as string == topic
            && tags.GetValueOrDefault("consumer") as string == consumerName
            && tags.GetValueOrDefault("outcome") as string == "error");
        Assert.True(errorOutcomes >= 2, $"Expected at least 2 error outcomes for {consumerName}; observed {errorOutcomes}.");

        var duration = exportedMetrics.Single(m => m.Name == "ago.platform.messaging.rabbitmq.consumer.duration");
        var durationTagSets = new List<Dictionary<string, object?>>();
        foreach (ref readonly var point in duration.GetMetricPoints())
        {
            durationTagSets.Add(TagsOf(point));
        }

        Assert.Contains(durationTagSets, tags =>
            tags.GetValueOrDefault("topic") as string == topic && tags.GetValueOrDefault("consumer") as string == consumerName);

        var deadLettered = exportedMetrics.Single(m => m.Name == "ago.platform.messaging.rabbitmq.dead_lettered");
        var deadLetterCount = SumMatchingLongPoints(deadLettered, tags => tags.GetValueOrDefault("event_type") as string == topic);
        Assert.Equal(1, deadLetterCount);
    }

    private static long SumMatchingLongPoints(Metric metric, Func<Dictionary<string, object?>, bool> matches)
    {
        long total = 0;
        foreach (ref readonly var point in metric.GetMetricPoints())
        {
            if (matches(TagsOf(point)))
            {
                total += point.GetSumLong();
            }
        }

        return total;
    }

    private static Dictionary<string, object?> TagsOf(in OpenTelemetry.Metrics.MetricPoint point)
    {
        var tags = new Dictionary<string, object?>();
        foreach (var tag in point.Tags)
        {
            tags[tag.Key] = tag.Value;
        }

        return tags;
    }
}
