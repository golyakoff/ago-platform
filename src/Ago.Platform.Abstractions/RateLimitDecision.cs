namespace Ago.Platform.Abstractions;

/// <summary><see cref="RetryAfter"/> is meaningful only when <see cref="Allowed"/> is
/// <see langword="false"/> - zero when allowed, never used by a caller either way.</summary>
public readonly record struct RateLimitDecision(bool Allowed, TimeSpan RetryAfter);
