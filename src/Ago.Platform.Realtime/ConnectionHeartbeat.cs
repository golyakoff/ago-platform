using Ago.Platform.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ago.Platform.Realtime;

/// <summary>
/// realtime.md: "Every key has a TTL and is refreshed by a heartbeat." Re-registers every
/// connection <see cref="LocalConnectionTracker"/> knows this node still holds - each call is the
/// same idempotent "extend the TTL" <see cref="IConnectionRegistry.RegisterAsync"/> already is for
/// an existing entry, so there is no separate "refresh" operation to keep in sync with "register."
/// </summary>
public sealed class ConnectionHeartbeat(
    IConnectionRegistry registry,
    LocalConnectionTracker tracker,
    NodeId currentNode,
    IOptions<ConnectionHeartbeatOptions> options,
    ILogger<ConnectionHeartbeat> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Value.Interval);
        do
        {
            try
            {
                await RefreshAllAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // concurrency.md: a BackgroundService catches and continues - one failed heartbeat
                // cycle must not permanently stop refreshing every other live connection's TTL.
                logger.LogError(ex, "Connection heartbeat cycle failed; retrying next cycle.");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task RefreshAllAsync(CancellationToken cancellationToken)
    {
        var snapshot = tracker.Snapshot();
        await Task.WhenAll(snapshot.Select(entry =>
            registry.RegisterAsync(entry.Key, currentNode, entry.Value, cancellationToken)));
    }
}
