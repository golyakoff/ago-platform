namespace Ago.Platform.Abstractions;

/// <summary>
/// A cache entry's identity. A plain <see langword="string"/> wrapper, not a Guid-backed id like
/// <see cref="NodeId"/> or <see cref="ConnectionId"/> - a cache key is a human-composed namespace
/// path (`caching.md`: e.g. <c>site-config:{public_key}</c>), never a generated identifier.
/// </summary>
public readonly record struct CacheKey(string Value)
{
    public override string ToString() => Value;
}
