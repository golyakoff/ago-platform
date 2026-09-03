using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace Ago.Platform.Messaging.RabbitMq;

/// <summary>
/// One connection for the process (RabbitMQ.Client best practice - channels are cheap, connections
/// are not). Automatic recovery is on, so a publisher or consumer built on top of this resumes
/// cleanly when the broker comes back, matching resilience.md's "outbox accumulates while it is
/// down" - this class is what makes "while it is down" survivable rather than fatal.
/// </summary>
public sealed class RabbitMqConnection(IOptions<RabbitMqOptions> options, ILogger<RabbitMqConnection> logger) : IAsyncDisposable
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private IConnection? _connection;

    public async Task<IChannel> CreateChannelAsync(CreateChannelOptions? channelOptions = null, CancellationToken cancellationToken = default)
    {
        var connection = await GetConnectionAsync(cancellationToken);
        return channelOptions is null
            ? await connection.CreateChannelAsync(cancellationToken: cancellationToken)
            : await connection.CreateChannelAsync(channelOptions, cancellationToken);
    }

    private async Task<IConnection> GetConnectionAsync(CancellationToken cancellationToken)
    {
        if (_connection is { IsOpen: true })
        {
            return _connection;
        }

        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (_connection is { IsOpen: true })
            {
                return _connection;
            }

            var settings = options.Value;
            var factory = new ConnectionFactory
            {
                HostName = settings.HostName,
                Port = settings.Port,
                UserName = settings.UserName,
                Password = settings.Password,
                VirtualHost = settings.VirtualHost,
                AutomaticRecoveryEnabled = true,
                TopologyRecoveryEnabled = true,
                // The client default (60s) means a silently-dead connection (broker paused or
                // network-partitioned, never sends TCP FIN/RST) can take minutes to be noticed before
                // automatic recovery even starts - found while proving 2-04's dispatcher actually
                // recovers after the broker comes back. Ten seconds is still generous for a real
                // network, and turns "recovery eventually happens" into "recovery happens within the
                // time this system's own latency targets care about" (nfr.md, Stage 7).
                RequestedHeartbeat = TimeSpan.FromSeconds(10),
            };

            _connection = await factory.CreateConnectionAsync(cancellationToken);
            return _connection;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_connection is not null)
            {
                await _connection.DisposeAsync();
            }
        }
        catch (Exception ex)
        {
            // Best-effort: disposal must never throw, whatever the broker is doing (17-09). The
            // client's own DisposeAsync already prefers a forced close over a negotiated one while
            // the connection is still open - RabbitMQ.Client's Connection.DisposeAsync calls its own
            // AbortAsync in that case, bounded to a 5s internal timeout - but a broker that never
            // answers even that forced-close handshake (paused, network-partitioned - never sends a
            // TCP FIN/RST, the exact "silently-dead connection" shape RequestedHeartbeat above exists
            // to catch) still lets a TaskCanceledException escape, because the client only swallows
            // OperationInterruptedException internally, not a timeout. Catching broadly here is what
            // lets a stopping host finish shutting down regardless of what the broker is doing;
            // logging rather than discarding silently is what leaves an operator debugging a wedged
            // shutdown something to find, instead of a shutdown that failed for reasons no log carries.
            logger.LogWarning(ex, "RabbitMQ connection did not close cleanly during dispose.");
        }
        finally
        {
            _lock.Dispose();
        }
    }
}
