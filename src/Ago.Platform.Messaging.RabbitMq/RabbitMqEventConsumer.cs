using System.Text;
using System.Text.Json;
using Ago.Platform.Abstractions;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Ago.Platform.Messaging.RabbitMq;

/// <summary>
/// One fanout exchange per topic (messaging.md: <c>Broadcast</c> needs "a per-node exclusive queue on
/// a fanout exchange" - <c>Competing</c> reuses the same exchange with one shared queue instead, which
/// behaves identically to any other exchange type when exactly one queue is bound). Deliberately not
/// yet the N-queue consistent-hash topology <c>concurrency.md</c> describes for per-key ordering at
/// scale - a single queue trivially preserves ordering by having only one consumer position, and the
/// in-process <c>ConversationSequencer</c> that scales it is a separate, later concern
/// (`concurrency.md`, not in this item's scope).
/// </summary>
public sealed class RabbitMqEventConsumer(RabbitMqConnection connection) : IEventConsumer
{
    public async Task SubscribeAsync(
        string topic,
        SubscriptionMode mode,
        RetryPolicy retryPolicy,
        Func<EventEnvelope, IMessageContext, CancellationToken, Task> handler,
        CancellationToken cancellationToken)
    {
        var channel = await connection.CreateChannelAsync(cancellationToken: cancellationToken);
        await channel.BasicQosAsync(0, prefetchCount: 50, global: false, cancellationToken);

        await channel.ExchangeDeclareAsync(topic, ExchangeType.Fanout, durable: true, cancellationToken: cancellationToken);

        var queueName = mode == SubscriptionMode.Competing ? topic : $"{topic}.{Guid.NewGuid():N}";
        var exclusive = mode == SubscriptionMode.Broadcast;
        await channel.QueueDeclareAsync(
            queue: queueName, durable: !exclusive, exclusive: exclusive, autoDelete: exclusive,
            cancellationToken: cancellationToken);
        await channel.QueueBindAsync(queueName, topic, routingKey: string.Empty, cancellationToken: cancellationToken);

        var retryQueueName = $"{queueName}.retry";
        await channel.QueueDeclareAsync(
            queue: retryQueueName, durable: true, exclusive: false, autoDelete: false,
            arguments: new Dictionary<string, object?>
            {
                ["x-message-ttl"] = (int)retryPolicy.InitialBackoff.TotalMilliseconds,
                ["x-dead-letter-exchange"] = "",
                ["x-dead-letter-routing-key"] = queueName,
            },
            cancellationToken: cancellationToken);

        await channel.QueueDeclareAsync(
            queue: retryPolicy.DeadLetterName, durable: true, exclusive: false, autoDelete: false,
            cancellationToken: cancellationToken);

        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += async (_, delivery) =>
        {
            var envelope = Deserialize(delivery);
            var attempt = GetAttempt(delivery);
            var context = new RabbitMqMessageContext(
                channel, delivery, retryQueueName, retryPolicy.DeadLetterName, retryPolicy.MaxAttempts, attempt);

            try
            {
                await handler(envelope, context, cancellationToken);
            }
            catch (Exception)
            {
                // messaging.md: handlers must be safe to run twice regardless of the inbox - a
                // thrown exception is treated exactly like an explicit NackAsync(requeue: true).
                await context.NackAsync(requeue: true, cancellationToken);
            }
        };

        await channel.BasicConsumeAsync(queueName, autoAck: false, consumer, cancellationToken);
    }

    private static int GetAttempt(BasicDeliverEventArgs delivery) =>
        delivery.BasicProperties.Headers?.TryGetValue("x-retry-attempt", out var value) == true && value is not null
            ? Convert.ToInt32(value)
            : 1;

    private static EventEnvelope Deserialize(BasicDeliverEventArgs delivery)
    {
        var props = delivery.BasicProperties;
        return new EventEnvelope(
            MessageId: Guid.Parse(props.MessageId ?? throw new InvalidOperationException("Delivery is missing MessageId.")),
            Type: props.Type ?? throw new InvalidOperationException("Delivery is missing Type."),
            Version: props.Headers?.TryGetValue("x-version", out var version) == true && version is not null
                ? Convert.ToInt32(version)
                : 1,
            PartitionKey: delivery.RoutingKey,
            OccurredAt: DateTimeOffset.FromUnixTimeSeconds(props.Timestamp.UnixTime),
            CorrelationId: Guid.Parse(props.CorrelationId ?? throw new InvalidOperationException("Delivery is missing CorrelationId.")),
            Payload: Encoding.UTF8.GetString(delivery.Body.Span));
    }
}
