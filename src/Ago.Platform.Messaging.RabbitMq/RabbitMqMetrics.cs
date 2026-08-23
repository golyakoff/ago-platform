using System.Diagnostics.Metrics;

namespace Ago.Platform.Messaging.RabbitMq;

/// <summary>
/// `7-02`: nfr.md's "RED metrics... per consumer" and "DLQ count" - instrumented here, at
/// <see cref="RabbitMqEventConsumer"/>'s own handler-invocation boundary (the same "{topic} process"
/// span `7-01` already named, `RabbitMqTracing`'s own remarks), not once per product consumer type.
/// This is the metrics mirror of that placement decision: a generic adapter-level wrapper covers
/// every consumer this platform or any product hosts - `Ago.Chat.Worker`'s
/// <c>ConnectionFanoutConsumer</c>/<c>UnreadCounterConsumer</c>/etc. need no changes of their own for
/// RED or DLQ counting to exist, the same "add it once, in the shared place, not per boundary"
/// reasoning `Ago.Platform.Resilience`'s own instruments in this item follow.
/// </summary>
internal static class RabbitMqMetrics
{
    public const string MeterName = "Ago.Platform.Messaging.RabbitMq";

    public const string ConsumerDurationInstrumentName = "ago.platform.messaging.rabbitmq.consumer.duration";

    public const string ConsumerCountInstrumentName = "ago.platform.messaging.rabbitmq.consumer.count";

    public const string DeadLetteredInstrumentName = "ago.platform.messaging.rabbitmq.dead_lettered";

    private static readonly Meter Meter = new(MeterName);

    private static readonly Histogram<double> ConsumerDuration = Meter.CreateHistogram<double>(
        ConsumerDurationInstrumentName,
        unit: "s",
        description: "Time spent inside a consumer's handler for one delivery, tagged by topic and consumer name.");

    private static readonly Counter<long> ConsumerCount = Meter.CreateCounter<long>(
        ConsumerCountInstrumentName,
        unit: "{delivery}",
        description: "Deliveries handled, tagged by topic, consumer name, and outcome (success/error).");

    private static readonly Counter<long> DeadLettered = Meter.CreateCounter<long>(
        DeadLetteredInstrumentName,
        unit: "{message}",
        description: "Messages that exhausted retries and were dead-lettered, tagged by event type.");

    public static void RecordHandled(string topic, string consumerName, TimeSpan duration, bool success)
    {
        var topicTag = new KeyValuePair<string, object?>("topic", topic);
        var consumerTag = new KeyValuePair<string, object?>("consumer", consumerName);
        ConsumerDuration.Record(duration.TotalSeconds, topicTag, consumerTag);
        ConsumerCount.Add(1, topicTag, consumerTag, new KeyValuePair<string, object?>("outcome", success ? "success" : "error"));
    }

    public static void RecordDeadLettered(string? eventType) =>
        DeadLettered.Add(1, new KeyValuePair<string, object?>("event_type", eventType ?? "unknown"));
}
