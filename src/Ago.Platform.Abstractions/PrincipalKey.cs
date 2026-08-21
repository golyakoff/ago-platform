namespace Ago.Platform.Abstractions;

/// <summary>
/// Who a set of connections belongs to, from the registry's point of view. Deliberately opaque -
/// "visitor" and "operator" are chat domain concepts the platform must not know
/// (clean-architecture.md's qualifying rule), so a caller builds this the same way a caller builds a
/// <c>CacheKey</c> for <c>ICache</c> (caching.md): e.g. <c>new PrincipalKey($"visitor:{visitorId}")</c>.
/// Two different products' principals never collide as long as each namespaces its own keys, which
/// is the caller's responsibility, not this type's.
/// </summary>
public readonly record struct PrincipalKey(string Value)
{
    public override string ToString() => Value;
}
