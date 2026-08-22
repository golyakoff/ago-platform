using Microsoft.Extensions.Diagnostics.HealthChecks;
using StackExchange.Redis;

namespace Ago.Platform.Caching.Redis;

/// <summary>
/// `3-06`: lives here, not a product's own project - a product using `ICache`/`IConnectionRegistry`
/// never sees `IConnectionMultiplexer` itself (`clean-architecture.md`'s dependency rule extended to
/// readiness, the same way `PersistenceBoundaryTests` keeps `Npgsql` inside
/// `Ago.Chat.Infrastructure.Postgres` alone) - a health check that pings Redis directly is exactly
/// the kind of code that belongs on this side of that boundary, and is generic besides.
/// </summary>
public sealed class RedisHealthCheck(IConnectionMultiplexer multiplexer) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await multiplexer.GetDatabase().PingAsync().WaitAsync(cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Cannot reach Redis.", ex);
        }
    }
}
