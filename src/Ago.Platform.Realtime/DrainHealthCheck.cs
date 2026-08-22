using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Ago.Platform.Realtime;

/// <summary>
/// `3-06`, `edge.md`: "readiness goes false while liveness stays true" the instant a node starts
/// draining - this is the "ready"-tagged check that makes that true. Generic, like
/// <see cref="DrainState"/> itself: any host holding hub connections wants the same behaviour, not
/// just `Ago.Chat.Api`.
/// </summary>
public sealed class DrainHealthCheck(DrainState drainState) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(drainState.IsDraining
            ? HealthCheckResult.Unhealthy("This node is draining.")
            : HealthCheckResult.Healthy());
}
