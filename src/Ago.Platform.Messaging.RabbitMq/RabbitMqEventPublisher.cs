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

            var properties = new BasicProperties
            {
                Persistent = true,
                MessageId = envelope.MessageId.ToString(),
                Type = envelope.Type,
                Timestamp = new AmqpTimestamp(envelope.OccurredAt.ToUnixTimeSeconds()),
                CorrelationId = envelope.CorrelationId.ToString(),
                Headers = new Dictionary<string, object?> { ["x-version"] = envelope.Version },
            };

            await channel.BasicPublishAsync(
                exchange: envelope.Type,
                routingKey: envelope.PartitionKey,
                mandatory: false,
                basicProperties: properties,
                body: Encoding.UTF8.GetBytes(envelope.Payload),
                cancellationToken: cancellationToken);
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
