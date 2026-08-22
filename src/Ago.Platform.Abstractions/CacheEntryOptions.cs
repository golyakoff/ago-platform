namespace Ago.Platform.Abstractions;

/// <summary>
/// `caching.md`: every TTL gets +/- 10% jitter so entries created together do not expire together -
/// applied once, uniformly, by the adapter (`Ago.Platform.Caching.Redis`), not by each call site.
/// <see cref="Ttl"/> here is the nominal value before jitter.
/// </summary>
public sealed record CacheEntryOptions(TimeSpan Ttl);
