namespace Ago.Platform.Abstractions;

/// <summary>
/// Publishes to the broker. Never called from inside a request handler - only the outbox dispatcher
/// calls this, after the fact the envelope describes has already committed (adr/0005).
/// </summary>
public interface IEventPublisher
{
    Task PublishAsync(EventEnvelope envelope, CancellationToken cancellationToken);
}
