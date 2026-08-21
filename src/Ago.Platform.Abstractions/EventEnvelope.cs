namespace Ago.Platform.Abstractions;

/// <summary>
/// The largest common denominator RabbitMQ and Kafka can both honour without lying
/// (docs/architecture/messaging.md, adr/0006): a topic + partition key + at-least-once fact, not a
/// generic envelope for arbitrary broker features. <see cref="Type"/> doubles as the topic name - an
/// adapter maps it to an exchange or a Kafka topic; there is no separate topic field to keep in sync.
/// </summary>
public sealed record EventEnvelope(
    Guid MessageId,
    string Type,
    int Version,
    string PartitionKey,
    DateTimeOffset OccurredAt,
    Guid CorrelationId,
    string Payload);
