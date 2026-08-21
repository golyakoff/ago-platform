namespace Ago.Platform.Abstractions;

/// <summary>
/// The explicit ack surface docs/architecture/messaging.md requires - auto-ack is never offered
/// through this port, because it would silently break the at-least-once/durability story the outbox
/// exists to guarantee.
/// </summary>
public interface IMessageContext
{
    Task AckAsync(CancellationToken cancellationToken);

    Task NackAsync(bool requeue, CancellationToken cancellationToken);

    Task DeadLetterAsync(CancellationToken cancellationToken);
}
