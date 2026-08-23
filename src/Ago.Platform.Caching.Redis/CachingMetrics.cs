using System.Diagnostics.Metrics;
using Ago.Platform.Abstractions;

namespace Ago.Platform.Caching.Redis;

/// <summary>
/// `7-02`: nfr.md's "cache hit ratio per key namespace" - a hit/miss counter, not a pre-divided ratio
/// gauge, since a ratio is a query-time aggregation (`rate(hits) / rate(hits + misses)`) any
/// Prometheus/Grafana reader already knows how to compute from two counters; baking the division in
/// here would only fix one specific time window's worth of ratio and throw away the raw counts a
/// dashboard actually wants. Public, dependency-free, the same "one shared static holder" shape as
/// <c>Ago.Chat.Contracts.ChatMetrics</c> - <see cref="RedisCache"/> is this Meter's only caller today.
///
/// The namespace tag is read straight off <see cref="CacheKey"/>'s own documented
/// convention (`caching.md`: a key is a human-composed <c>{namespace}:{id}</c> path, e.g.
/// <c>site-config:{public_key}</c>) - generic string parsing, not a product concept, which is why
/// this lives beside <see cref="RedisCache"/> in the platform rather than needing chat's own
/// `SiteCacheKeys` to declare its namespace a second time just for metrics.
/// </summary>
public static class CachingMetrics
{
    public const string MeterName = "Ago.Platform.Caching.Redis";

    public const string CacheAccessInstrumentName = "ago.platform.caching.redis.cache_access";

    private static readonly Meter Meter = new(MeterName);

    private static readonly Counter<long> CacheAccessCounter = Meter.CreateCounter<long>(
        CacheAccessInstrumentName,
        unit: "{access}",
        description: "Cache reads, tagged by key namespace and outcome (hit/miss).");

    public static void RecordAccess(CacheKey key, bool hit) =>
        CacheAccessCounter.Add(
            1,
            new KeyValuePair<string, object?>("namespace", Namespace(key)),
            new KeyValuePair<string, object?>("outcome", hit ? "hit" : "miss"));

    /// <summary>The part of a key before its first ':' - <see cref="CacheKey"/>'s own
    /// documented shape. A key with no ':' at all (never produced by any real caller today) reports
    /// as its own whole value rather than throwing - a metrics tag should never be the reason a cache
    /// read fails.</summary>
    private static string Namespace(CacheKey key)
    {
        var value = key.Value;
        var separator = value.IndexOf(':');
        return separator < 0 ? value : value[..separator];
    }
}
