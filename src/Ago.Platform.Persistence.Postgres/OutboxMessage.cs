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
        Guid correlationId, string? traceContext = null)
    {
        Id = id;
        OccurredAt = occurredAt;
        Type = type;
        Version = version;
        Payload = payload;
        PartitionKey = partitionKey;
        CorrelationId = correlationId;
        TraceContext = traceContext;
    }

    public Guid Id { get; private set; }

    public DateTimeOffset OccurredAt { get; private set; }

    public string Type { get; private set; } = null!;

    public int Version { get; private set; }

    public string Payload { get; private set; } = null!;

    public string PartitionKey { get; private set; } = null!;

    public Guid CorrelationId { get; private set; }

    /// <summary>`7-01`: the W3C `traceparent` of the trace this row's event describes, captured by
    /// the caller at write time (<see cref="Ago.Platform.Abstractions.IOutboxWriter.Enqueue"/>'s own
    /// remarks) so the outbox dispatcher can re-parent a real child span to it at publish time
    /// instead of starting a fresh, disconnected trace (messaging.md: "the trace id captured at the
    /// write must survive the poll-and-publish handoff"). Null for any row staged before this
    /// shipped, or by a caller this item did not instrument - the dispatcher treats that exactly
    /// like today, publishing with no parent.</summary>
    public string? TraceContext { get; private set; }

    public DateTimeOffset? PublishedAt { get; private set; }

    public int Attempts { get; private set; }

    public void MarkPublished(DateTimeOffset publishedAt) => PublishedAt = publishedAt;

    public void IncrementAttempts() => Attempts++;
}
