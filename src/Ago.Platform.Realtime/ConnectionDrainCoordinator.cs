using System.Text.Json;
using Ago.Platform.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ago.Platform.Realtime;

/// <summary>
/// `3-06`: `concurrency.md`'s graceful-shutdown sequence, the connection-holding half. Registered
/// via <c>IHostApplicationLifetime.ApplicationStopping</c> so <see cref="DrainState"/> flips the
/// instant shutdown begins, then does the real work from an overridden <see cref="StopAsync"/> -
/// unusual for a <see cref="BackgroundService"/> in this codebase (every other one just runs a loop
/// until cancelled), but exactly the seam .NET's generic host gives a hosted service to do work
/// *during* shutdown, awaited up to <c>HostOptions.ShutdownTimeout</c>
/// (`terminationGracePeriodSeconds`-derived).
///
/// "Stop accepting new hub connections" is not this class's job - it just flips
/// <see cref="DrainState"/>; each hub's own `OnConnectedAsync` is what checks it and aborts before
/// registering (`Ago.Chat.Api`'s `VisitorHub`/`OperatorHub`).
/// </summary>
public sealed class ConnectionDrainCoordinator(
    LocalConnectionTracker connectionTracker,
    ILocalConnectionDispatcher dispatcher,
    IConnectionRegistry registry,
    NodeId currentNode,
    IHostApplicationLifetime lifetime,
    DrainState drainState,
    IOptions<DrainOptions> options,
    ILogger<ConnectionDrainCoordinator> logger) : BackgroundService
{
    // A field initializer, not a line inside ExecuteAsync: BackgroundService.StartAsync returns as
    // soon as ExecuteAsync has been *scheduled*, not once it has actually run its first statement -
    // registering here instead makes it happen synchronously during construction, before this
    // service can even be started, closing a real race (found by running the full suite, not the
    // single test alone: the registration sometimes lost the race against a test - or in
    // production, a real shutdown - signalling ApplicationStopping first).
    private readonly IDisposable _stoppingRegistration = lifetime.ApplicationStopping.Register(drainState.MarkDraining);

    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.Delay(Timeout.Infinite, stoppingToken);

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        var connections = connectionTracker.Snapshot();
        logger.LogInformation("Draining {Count} connection(s) from node {NodeId}.", connections.Count, currentNode);

        foreach (var (connectionId, _) in connections)
        {
            try
            {
                // Per-connection jitter, not one shared delay - the whole point (DrainOptions' own
                // remarks).
                var after = TimeSpan.FromSeconds(Random.Shared.NextDouble() * options.Value.MaxReconnectJitter.TotalSeconds);
                var payload = JsonSerializer.Serialize(new { after });
                await dispatcher.DispatchAsync(connectionId, "Reconnect", payload, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // realtime.md: a failed delivery is harmless, the same "advice, not truth" contract
                // applied to shutdown - one connection failing to hear the hint must not stop the
                // rest of the batch, and the registry cleanup below happens regardless.
                logger.LogDebug(ex, "Failed to tell connection {ConnectionId} to reconnect - continuing drain.", connectionId);
            }
        }

        await registry.RemoveNodeAsync(currentNode, cancellationToken);

        using var timeoutCts = new CancellationTokenSource(options.Value.DrainTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        try
        {
            while (connectionTracker.Snapshot().Count > 0)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(200), linked.Token);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The drain's own bounded timeout fired, not the host's cancellation - never worth
            // blocking shutdown over a client that never reconnected.
            logger.LogInformation(
                "Drain timed out with {Count} connection(s) still open on node {NodeId} - proceeding with shutdown.",
                connectionTracker.Snapshot().Count, currentNode);
        }

        await base.StopAsync(cancellationToken);
    }
}
