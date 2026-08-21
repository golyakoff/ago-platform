namespace Ago.Platform.Abstractions;

/// <summary>
/// Consumer-side dedup ledger (docs/architecture/messaging.md). Unlike <see cref="IOutboxWriter"/>,
/// this saves - detecting "this message was already processed" is only knowable from the outcome of
/// an actual write (a unique-constraint violation), so the consumer's own work must already be
/// staged on the same tracked context before calling this, and this call is what commits both
/// together (adr/0017). A caller that calls this before staging its own work breaks the
/// same-transaction guarantee silently; the method name is deliberately explicit about saving, to
/// make that mistake harder to make by accident.
/// </summary>
public interface IInboxChecker
{
    /// <returns>
    /// <c>true</c> if this was the first delivery and everything staged on the context (including
    /// the caller's own work) was persisted; <c>false</c> if <paramref name="messageId"/> was already
    /// recorded for <paramref name="consumer"/> - in which case nothing from this call, including the
    /// caller's own staged work, was persisted, and the caller should ack and return without acting.
    /// </returns>
    Task<bool> TryRecordAndSaveAsync(Guid messageId, string consumer, CancellationToken cancellationToken);
}
