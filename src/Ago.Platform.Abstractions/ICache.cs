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
///
/// `where T : class` on every method - found live, not designed in from the start: for an
/// *unconstrained* generic parameter, C#'s `T?` return annotation has no runtime effect when `T` is
/// instantiated with a value type (confirmed empirically - `default(T?)` for `T = bool` is `false`,
/// not a distinguishable null), so a cache miss and a genuinely-cached `false`/`0` become
/// indistinguishable to every caller, including this port's own `GetOrCreateAsync` (its `is { }`
/// checks silently treat a cold key as a cached `false` and never call the factory at all). Every
/// caller up to this point happened to avoid the bug by only ever caching reference-type DTOs
/// (`GetSiteConfigByPublicKeyHandler`'s `SiteLookupResult`); the constraint turns "silently wrong for
/// a value type" into a compile error instead, which is the honest fix - the alternative (wrapping
/// every cached value in an internal presence-envelope) would remove the constraint but adds
/// complexity no real caller has needed yet.
/// </summary>
public interface ICache
{
    Task<T?> GetAsync<T>(CacheKey key, CancellationToken cancellationToken) where T : class;

    Task SetAsync<T>(CacheKey key, T value, CacheEntryOptions options, CancellationToken cancellationToken) where T : class;

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
        CacheKey key, Func<CancellationToken, Task<T>> factory, CacheEntryOptions options, CancellationToken cancellationToken)
        where T : class;

    Task RemoveAsync(CacheKey key, CancellationToken cancellationToken);
}
