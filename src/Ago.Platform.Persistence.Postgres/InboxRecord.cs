namespace Ago.Platform.Persistence.Postgres;

/// <summary>
/// EF-mapped shape of docs/architecture/data-model.md's <c>inbox</c> table - the idempotency ledger.
/// Keyed by (<see cref="MessageId"/>, <see cref="Consumer"/>) rather than <see cref="MessageId"/>
/// alone, because one message can legitimately be processed by more than one independent consumer
/// (e.g. the unread-counter consumer and a future, unrelated one), each needing its own dedup record.
/// </summary>
public sealed class InboxRecord
{
    private InboxRecord()
    {
    }

    public InboxRecord(Guid messageId, string consumer, DateTimeOffset processedAt)
    {
        MessageId = messageId;
        Consumer = consumer;
        ProcessedAt = processedAt;
    }

    public Guid MessageId { get; private set; }

    public string Consumer { get; private set; } = null!;

    public DateTimeOffset ProcessedAt { get; private set; }
}
