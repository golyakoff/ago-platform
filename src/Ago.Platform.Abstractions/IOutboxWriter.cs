namespace Ago.Platform.Abstractions;

/// <summary>
/// Stages an event to be published, on whichever <c>DbContext</c> is already tracking the caller's
/// own change - it performs no I/O itself (adr/0005, adr/0017). The caller's own single
/// <c>SaveChangesAsync</c> call is what makes "same transaction as the state change" true; this
/// method exists so a handler never has to know the outbox row's shape to stage one.
/// </summary>
public interface IOutboxWriter
{
    void Enqueue(EventEnvelope envelope);
}
