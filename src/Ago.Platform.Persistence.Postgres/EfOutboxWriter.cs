using Ago.Platform.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Ago.Platform.Persistence.Postgres;

/// <summary>
/// adr/0017: generic over <typeparamref name="TContext"/> so this project never references a
/// product's concrete <c>DbContext</c>. Stages only - the caller's own <c>SaveChangesAsync</c> is
/// what commits this together with whatever state change it describes.
/// </summary>
public sealed class EfOutboxWriter<TContext>(TContext dbContext) : IOutboxWriter
    where TContext : DbContext
{
    public void Enqueue(EventEnvelope envelope)
    {
        var message = new OutboxMessage(
            envelope.MessageId,
            envelope.OccurredAt,
            envelope.Type,
            envelope.Payload,
            envelope.PartitionKey);

        dbContext.Set<OutboxMessage>().Add(message);
    }
}
