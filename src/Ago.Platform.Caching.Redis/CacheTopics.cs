namespace Ago.Platform.Caching.Redis;

/// <summary>
/// `messaging.md`'s Topics table names <c>CacheInvalidated</c> directly - unlike
/// <c>Ago.Platform.Realtime.NodeTopics</c>, there is exactly one topic here, not one per node, since
/// every node subscribes to the same broadcast (`SubscriptionMode.Broadcast`'s own doc comment
/// already names this event as its one intended use).
/// </summary>
public static class CacheTopics
{
    public const string Invalidated = "CacheInvalidated";
}
