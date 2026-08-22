namespace Ago.Platform.Caching.Redis;

/// <summary>The broadcast payload: the one key every node must drop. Deliberately just a key, not a
/// reason or a value - a node's own <see cref="Ago.Platform.Abstractions.ICache"/> already knows how
/// to remove a key; it does not need to know why.</summary>
public sealed record CacheInvalidated(string Key);
