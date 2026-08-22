namespace Ago.Platform.Abstractions;

/// <summary>
/// `caching.md`'s cache-aside port. Deliberately not `IDistributedCache`: that port's byte-array API
/// pushes serialisation onto every caller and has no single-flight story. Application code never sees
/// `IConnectionMultiplexer`, a Redis key string, or a serializer - the same dependency rule as every
/// other external resource (`clean-architecture.md`).
///
/// Every method degrades to a cache miss on a Redis failure - never throws - per `adr/0009` and
/// `resilience.md`'s Redis row: losing Redis makes reads slower (a `GetOrCreateAsync` falls through to
/// its factory every time), never wrong.
/// </summary>
public interface ICache
{
    Task<T?> GetAsync<T>(CacheKey key, CancellationToken cancellationToken);

    Task SetAsync<T>(CacheKey key, T value, CacheEntryOptions options, CancellationToken cancellationToken);

    /// <summary>
    /// The method most call sites should use - the only one with stampede protection (in-process
    /// single-flight plus a short cross-node Redis lock, `caching.md`'s Patterns section). Returns the
    /// cached value if present, otherwise calls <paramref name="factory"/> exactly once per cold key
    /// (per this process; the cross-node lock narrows, but does not guarantee, the same across nodes)
    /// and populates the cache with <paramref name="options"/>.
    ///
    /// If <paramref name="factory"/> itself already wrote <paramref name="key"/> via
    /// <see cref="SetAsync{T}"/> before returning, that write is left alone rather than immediately
    /// overwritten with <paramref name="options"/> - the hook a caller needing a different TTL for a
    /// particular outcome (e.g. a short negative-cache TTL for "not found", `caching.md`) uses instead
    /// of a second cache-entry-options parameter this port does not have.
    /// </summary>
    Task<T> GetOrCreateAsync<T>(
        CacheKey key, Func<CancellationToken, Task<T>> factory, CacheEntryOptions options, CancellationToken cancellationToken);

    Task RemoveAsync(CacheKey key, CancellationToken cancellationToken);
}
