namespace Ago.Platform.Realtime;

/// <summary>Bound from <c>Realtime:ConnectionRegistry:*</c> config keys, validated at startup
/// (naming-and-structure.md's options-validation rule).</summary>
public sealed class ConnectionRegistryOptions
{
    public const string SectionName = "Realtime:ConnectionRegistry";

    /// <summary>realtime.md: "Every key has a TTL and is refreshed by a heartbeat." Must be
    /// comfortably longer than <see cref="ConnectionHeartbeatOptions.Interval"/> so a couple of
    /// missed beats (a GC pause, a slow Redis round trip) do not expire a live connection.</summary>
    public TimeSpan EntryTtl { get; set; } = TimeSpan.FromSeconds(30);
}
