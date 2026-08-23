namespace Ago.Platform.Abstractions;

/// <summary>
/// Stages an event to be published, on whichever <c>DbContext</c> is already tracking the caller's
/// own change - it performs no I/O itself (adr/0005, adr/0017). The caller's own single
/// <c>SaveChangesAsync</c> call is what makes "same transaction as the state change" true; this
/// method exists so a handler never has to know the outbox row's shape to stage one.
/// </summary>
public interface IOutboxWriter
{
    /// <summary>
    /// `7-01`: <paramref name="traceContext"/> is the W3C `traceparent` of whatever trace this event
    /// describes the outcome of - an explicit parameter, not read from an ambient
    /// <c>System.Diagnostics.Activity.Current</c> here, because the write side can genuinely batch
    /// several unrelated messages into one physical commit (`Ago.Chat`'s pipeline batch writer):
    /// reading an ambient value at enqueue time would tag every row in a batch with whichever trace
    /// happened to be current for the *last* one staged, not its own. A caller with no trace to
    /// attach (nothing in this item's scope instruments yet) simply omits it - the outbox dispatcher
    /// then publishes with no parent, a fresh root, exactly today's behaviour.
    /// </summary>
    void Enqueue(EventEnvelope envelope, string? traceContext = null);
}
