using System.Text;
using Ago.Platform.Abstractions;
using RabbitMQ.Client;

namespace Ago.Platform.Messaging.RabbitMq;

/// <summary>
/// `EventEnvelope.Type` is the exchange name (adr/0006: the port exposes topic + partition key,
/// nothing broker-specific) - a fanout exchange per topic, so every subscribing queue (competing or
/// broadcast, `RabbitMqEventConsumer`) gets every message regardless of routing key; the routing key
/// still carries the partition key on the message itself for a future N-queue sharded topology
/// (`RabbitMqEventConsumer`'s remarks). Publisher confirms (resilience.md: "publisher confirms" for
/// this boundary) mean this method does not return until the broker has actually accepted the
/// message.
/// </summary>
public sealed class RabbitMqEventPublisher(RabbitMqConnection connection) : IEventPublisher, IAsyncDisposable
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private IChannel? _channel;

    public async Task PublishAsync(EventEnvelope envelope, CancellationToken cancellationToken)
    {
        var channel = await GetChannelAsync(cancellationToken);

        await _lock.WaitAsync(cancellationToken);
        try
        {
            await channel.ExchangeDeclareAsync(envelope.Type, ExchangeType.Fanout, durable: true, cancellationToken: cancellationToken);

            var headers = new Dictionary<string, object?> { ["x-version"] = envelope.Version };

            // `7-01`: whatever Activity is current when this call is made carries the trace this
            // publish belongs to (RabbitMqTracing's own remarks - the caller is expected to have
            // already started one, parented from wherever the trace actually originated: the outbox
            // dispatcher's `ago.chat.outbox.dispatch`, or a fan-out consumer's own ambient activity
            // for the second, ephemeral publish realtime.md's Fan-out path makes). No current
            // Activity (nothing is sampling, or a caller this item never reached) means no header -
            // a consumer that finds none simply starts a fresh trace, same as today.
            if (System.Diagnostics.Activity.Current?.Id is { } traceParent)
            {
                headers[RabbitMqTracing.TraceParentHeader] = traceParent;
            }

            var properties = new BasicProperties
            {
                Persistent = true,
                MessageId = envelope.MessageId.ToString(),
                Type = envelope.Type,
                Timestamp = new AmqpTimestamp(envelope.OccurredAt.ToUnixTimeSeconds()),
                CorrelationId = envelope.CorrelationId.ToString(),
                Headers = headers,
            };

            await channel.BasicPublishAsync(
                exchange: envelope.Type,
                routingKey: envelope.PartitionKey,
                mandatory: false,
                basicProperties: properties,
                body: Encoding.UTF8.GetBytes(envelope.Payload),
                cancellationToken: cancellationToken);
        }
        catch
        {
            // A publish that faulted or was cancelled (timeout waiting on a confirm) leaves this
            // channel's state ambiguous - IChannel.IsOpen was observed staying true for a full 60s+
            // after a broker outage genuinely ended, so it cannot be trusted to decide whether the
            // next attempt should reuse this channel. Discarding it forces GetChannelAsync to
            // negotiate a fresh one, which is cheap and known-good, rather than keep retrying on a
            // channel that already failed once for an unknown reason.
            if (_channel is not null)
            {
                await _channel.DisposeAsync();
                _channel = null;
            }

            throw;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<IChannel> GetChannelAsync(CancellationToken cancellationToken)
    {
        if (_channel is { IsOpen: true })
        {
            return _channel;
        }

        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (_channel is { IsOpen: true })
            {
                return _channel;
            }

            _channel = await connection.CreateChannelAsync(
                new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true),
                cancellationToken);
            return _channel;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_channel is not null)
        {
            await _channel.DisposeAsync();
        }

        _lock.Dispose();
    }
}
