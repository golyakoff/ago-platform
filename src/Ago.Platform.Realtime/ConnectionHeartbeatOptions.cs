namespace Ago.Platform.Realtime;

/// <summary>Bound from <c>Realtime:ConnectionHeartbeat:*</c> config keys, validated at startup
/// (naming-and-structure.md's options-validation rule).</summary>
public sealed class ConnectionHeartbeatOptions
{
    public const string SectionName = "Realtime:ConnectionHeartbeat";

    /// <summary>Default (10s) is a third of <see cref="ConnectionRegistryOptions.EntryTtl"/>'s
    /// default (30s) - three missed beats in a row before a live connection's entry could lapse.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(10);
}
