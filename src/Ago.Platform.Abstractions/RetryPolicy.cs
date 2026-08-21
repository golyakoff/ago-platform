namespace Ago.Platform.Abstractions;

/// <summary>
/// Declared per subscription, not hardcoded in an adapter (docs/architecture/messaging.md: "Retry
/// and dead-lettering are declared per subscription"). RabbitMQ implements this with a delay/retry
/// exchange, Kafka with a retry topic - genuinely different mechanisms behind one intent (adr/0006).
/// </summary>
public sealed record RetryPolicy(int MaxAttempts, TimeSpan InitialBackoff, string DeadLetterName);
