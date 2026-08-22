namespace Ago.Platform.Realtime;

/// <summary>
/// `3-06`: whether this node is currently shedding load. A plain flag, not a port - nothing outside
/// this process needs to know a node is draining, only this node's own readiness check and its own
/// hubs (which stop accepting new connections once it flips). Set exactly once, by
/// <see cref="ConnectionDrainCoordinator"/> registering against
/// <c>IHostApplicationLifetime.ApplicationStopping</c> - synchronously, the instant shutdown begins,
/// deliberately decoupled from any hosted service's own <c>StopAsync</c> ordering so readiness goes
/// false as early as possible (`edge.md`: "readiness goes false while liveness stays true").
/// </summary>
public sealed class DrainState
{
    private volatile bool _isDraining;

    public bool IsDraining => _isDraining;

    public void MarkDraining() => _isDraining = true;
}
