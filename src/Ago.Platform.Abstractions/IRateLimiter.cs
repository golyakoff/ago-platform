namespace Ago.Platform.Abstractions;

/// <summary>
/// `caching.md`'s "Rate limiting and counters" section: application code asks "may this happen?" and
/// gets a decision plus a retry-after; it never touches Redis, a Lua script, or a bucket's storage
/// shape directly (`clean-architecture.md`'s dependency rule, same as every other external resource).
///
/// A failed check (Redis unreachable) degrades to <c>Allowed: true</c>, never an error surfaced to
/// the caller and never a false deny - `adr/0009` already names this: "a counter whose loss is
/// acceptable (rate limits fail open to the next window)." Losing rate limiting under a Redis outage
/// is an accepted, bounded cost; falsely rejecting real traffic because Redis is down is not.
/// </summary>
public interface IRateLimiter
{
    Task<RateLimitDecision> CheckAsync(RateLimitKey key, RateLimitRule rule, CancellationToken cancellationToken);
}
