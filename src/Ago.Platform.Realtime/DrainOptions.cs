namespace Ago.Platform.Realtime;

/// <summary>Bound from <c>Realtime:Drain:*</c> config keys, validated at startup
/// (naming-and-structure.md's options-validation rule). Defaults are not measured or load-tested -
/// a starting point for Stage 7 to tune, same caveat as every other unmeasured constant in this
/// codebase (CLAUDE.md: "measure or stay silent").</summary>
public sealed class DrainOptions
{
    public const string SectionName = "Realtime:Drain";

    /// <summary>Upper bound of the jitter told to each departing connection - `edge.md`: "without
    /// jitter, a rolling restart becomes a self-inflicted thundering herd." Each connection gets its
    /// own random delay in <c>[0, MaxReconnectJitter]</c>, not one shared value.</summary>
    public TimeSpan MaxReconnectJitter { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>How long <see cref="ConnectionDrainCoordinator"/> waits for connections to actually
    /// drop before giving up and letting the host stop anyway - never blocks shutdown
    /// indefinitely on a client that never reconnects.</summary>
    public TimeSpan DrainTimeout { get; set; } = TimeSpan.FromSeconds(20);
}
