namespace Ago.Platform.Persistence.Postgres;

/// <summary>
/// EF-mapped shape of docs/architecture/data-model.md's <c>outbox</c> table. Staged by
/// <see cref="EfOutboxWriter{TContext}"/>, never constructed by product code directly - the port a
/// handler calls is <c>Ago.Platform.Abstractions.IOutboxWriter</c>.
/// </summary>
public sealed class OutboxMessage
{
    private OutboxMessage()
    {
    }

    public OutboxMessage(
        Guid id, DateTimeOffset occurredAt, string type, int version, string payload, string partitionKey,
        Guid correlationId)
    {
        Id = id;
        OccurredAt = occurredAt;
        Type = type;
        Version = version;
        Payload = payload;
        PartitionKey = partitionKey;
        CorrelationId = correlationId;
    }

    public Guid Id { get; private set; }

    public DateTimeOffset OccurredAt { get; private set; }

    public string Type { get; private set; } = null!;

    public int Version { get; private set; }

    public string Payload { get; private set; } = null!;

    public string PartitionKey { get; private set; } = null!;

    public Guid CorrelationId { get; private set; }

    public DateTimeOffset? PublishedAt { get; private set; }

    public int Attempts { get; private set; }

    public void MarkPublished(DateTimeOffset publishedAt) => PublishedAt = publishedAt;

    public void IncrementAttempts() => Attempts++;
}
