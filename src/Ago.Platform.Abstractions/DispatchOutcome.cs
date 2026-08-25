namespace Ago.Platform.Abstractions;

/// <summary>
/// What happened to one <see cref="ILocalConnectionDispatcher.DispatchAsync"/> call, as reported by
/// the only code that can actually know: the host's own dispatcher.
///
/// `7-08`: the port already said "a connection this process no longer knows about is not an error,
/// simply do nothing for it" - it just gave the caller no way to tell that apart from a real push,
/// which is why "did the server even try to deliver to that connection" was unanswerable from the
/// running system. The outcome is returned rather than counted by the caller against some proxy for
/// the same fact (the node's <c>LocalConnectionTracker</c>, say), because a proxy is only right for
/// as long as every implementation happens to agree with it - the shape of defect `7-07` found in
/// the connections gauge.
/// </summary>
public enum DispatchOutcome
{
    /// <summary>This process held the connection and handed the payload to its transport. Not a
    /// claim that the client received it - no transport this port abstracts over can promise that
    /// synchronously, and realtime.md never asked it to.</summary>
    Delivered,

    /// <summary>This process does not hold that connection (it disconnected, or the registry entry
    /// was stale). Nothing was pushed, and nothing is wrong: realtime.md's "a stale entry causes a
    /// harmless failed delivery."</summary>
    ConnectionNotLocal,
}
