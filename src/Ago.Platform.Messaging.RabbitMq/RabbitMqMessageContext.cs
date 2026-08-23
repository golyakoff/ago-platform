using Ago.Platform.Abstractions;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Ago.Platform.Messaging.RabbitMq;

/// <summary>
/// Turns one delivery's ack/nack/dead-letter decision into the delay-queue-and-DLX retry pattern
/// (messaging.md: "N attempts with exponential backoff, then dead-letter with the full envelope and
/// the last exception") - a raw RabbitMQ nack-with-requeue would hot-loop the same message with no
/// delay, which is not what "backoff" means.
/// </summary>
internal sealed class RabbitMqMessageContext(
    IChannel channel,
    BasicDeliverEventArgs delivery,
    string retryQueueName,
    string deadLetterQueueName,
    int maxAttempts,
    int attempt) : IMessageContext
{
    public Task AckAsync(CancellationToken cancellationToken) =>
        channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, cancellationToken).AsTask();

    public async Task NackAsync(bool requeue, CancellationToken cancellationToken)
    {
        if (!requeue || attempt >= maxAttempts)
        {
            await DeadLetterAsync(cancellationToken);
            return;
        }

        var headers = delivery.BasicProperties.Headers is null
            ? new Dictionary<string, object?>()
            : new Dictionary<string, object?>(delivery.BasicProperties.Headers);
        headers["x-retry-attempt"] = attempt + 1;

        var properties = new BasicProperties(delivery.BasicProperties) { Headers = headers };
        await channel.BasicPublishAsync(
            exchange: "", routingKey: retryQueueName, mandatory: false,
            basicProperties: properties, body: delivery.Body, cancellationToken: cancellationToken);
        await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, cancellationToken);
    }

    public async Task DeadLetterAsync(CancellationToken cancellationToken)
    {
        await channel.BasicPublishAsync(
            exchange: "", routingKey: deadLetterQueueName, mandatory: false,
            basicProperties: new BasicProperties(delivery.BasicProperties), body: delivery.Body, cancellationToken: cancellationToken);
        await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, cancellationToken);

        // `7-02`: messaging.md's "DLQ count" - the single choke point every dead-letter decision
        // passes through, whether reached from NackAsync giving up above or called directly by a
        // handler. delivery.BasicProperties.Type is the same event-type string EventEnvelope.Type was
        // deserialized from (RabbitMqEventConsumer.Deserialize), read straight off the wire rather
        // than re-parsing the envelope here.
        RabbitMqMetrics.RecordDeadLettered(delivery.BasicProperties.Type);
    }
}
