namespace Ago.Platform.Abstractions;

/// <summary>
/// Publishes to the broker. Two legitimate callers, never a third without adding one to this list:
/// the outbox dispatcher, publishing an event whose fact has already committed (adr/0005) - and
/// a purely derived, ephemeral notification computed from an already-outboxed event, where losing
/// the publish loses nothing durable (adr/0020 - e.g. realtime.md's node-delivery fan-out). Never
/// called directly from a request handler, and never for an event describing a state change that has
/// not committed yet.
/// </summary>
public interface IEventPublisher
{
    Task PublishAsync(EventEnvelope envelope, CancellationToken cancellationToken);
}
