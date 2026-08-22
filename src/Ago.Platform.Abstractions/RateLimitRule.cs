namespace Ago.Platform.Abstractions;

/// <summary>
/// The bucket's own parameters, supplied by the caller on every check rather than configured inside
/// the port - <c>caching.md</c>: "no domain concept in the port itself." Two different call sites
/// checking the same key concept (e.g. two different message-send limits) would still be free to use
/// different rules; nothing here assumes there is exactly one rule per key shape.
/// </summary>
public sealed record RateLimitRule(int Capacity, double RefillPerSecond);
