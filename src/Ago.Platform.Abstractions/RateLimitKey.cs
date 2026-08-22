namespace Ago.Platform.Abstractions;

/// <summary>A rate-limit bucket's identity - the caller composes it (e.g.
/// <c>message-send:visitor:{id}</c>), the same "opaque to the port" shape <see cref="CacheKey"/>
/// already uses.</summary>
public readonly record struct RateLimitKey(string Value)
{
    public override string ToString() => Value;
}
