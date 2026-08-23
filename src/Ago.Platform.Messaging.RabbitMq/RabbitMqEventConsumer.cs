using System.Diagnostics;
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
///
/// `5-11`: a `Competing` queue used to be named after the bare topic, with no way to tell "another
/// replica of the same consumer" (correct - both belong on this queue) from "a completely different
/// consumer type that also happens to subscribe to this topic" (wrong - each needs its own copy of
/// every message) apart. Naming it `{topic}.{consumerName}` fixes that: replicas of one logical
/// consumer pass the same name and correctly share a queue; two independent consumer types pass
/// different names and correctly get one queue each.
/// </summary>
public sealed class RabbitMqEventConsumer(RabbitMqConnection connection) : IEventConsumer
{
    public async Task SubscribeAsync(
        string topic,
        SubscriptionMode mode,
        string consumerName,
        RetryPolicy retryPolicy,
        Func<EventEnvelope, IMessageContext, CancellationToken, Task> handler,
        CancellationToken cancellationToken)
    {
        var channel = await connection.CreateChannelAsync(cancellationToken: cancellationToken);
        await channel.BasicQosAsync(0, prefetchCount: 50, global: false, cancellationToken);

        await channel.ExchangeDeclareAsync(topic, ExchangeType.Fanout, durable: true, cancellationToken: cancellationToken);

        var queueName = mode == SubscriptionMode.Competing ? $"{topic}.{consumerName}" : $"{topic}.{Guid.NewGuid():N}";
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

            // `7-02`: nfr.md's "RED metrics... per consumer" - the same duration/success/error triad
            // 7-01's own manual hub spans record, at this adapter's own generic handler-invocation
            // boundary rather than once per product consumer type (RabbitMqMetrics's own remarks).
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            // `7-01`: RabbitMqTracing's own remarks - extracts the `traceparent` header the
            // publisher injected and starts a real child span in that same trace, named after the
            // topic (OTel's own messaging semantic-convention shape, "{destination} process") so it
            // reads correctly in Jaeger without this adapter needing to know what a consumer does
            // with the message. Every specific consumer's own manual span (if any) nests inside this
            // one automatically, since Activity.Current is what the handler below actually runs
            // under - a genuine ambient parent, not a value the handler has to thread through itself.
            RabbitMqTracing.TryParseTraceParent(GetTraceParent(delivery), out var parentContext);
            using var activity = RabbitMqTracing.Source.StartActivity(
                $"{topic} process", ActivityKind.Consumer, parentContext);

            try
            {
                await handler(envelope, context, cancellationToken);
                RabbitMqMetrics.RecordHandled(topic, consumerName, stopwatch.Elapsed, success: true);
            }
            catch (Exception)
            {
                RabbitMqMetrics.RecordHandled(topic, consumerName, stopwatch.Elapsed, success: false);

                // messaging.md: handlers must be safe to run twice regardless of the inbox - a
                // thrown exception is treated exactly like an explicit NackAsync(requeue: true).
                await context.NackAsync(requeue: true, cancellationToken);
            }
        };

        await channel.BasicConsumeAsync(queueName, autoAck: false, consumer, cancellationToken);
    }

    // RabbitMQ.Client round-trips a header string as a raw byte[] on receive (AMQP's "long string"
    // table encoding), not the string it was set with on publish - the same reason x-retry-attempt
    // below goes through Convert rather than a direct cast.
    private static string? GetTraceParent(BasicDeliverEventArgs delivery) =>
        delivery.BasicProperties.Headers?.TryGetValue(RabbitMqTracing.TraceParentHeader, out var value) == true && value is not null
            ? value switch { byte[] bytes => Encoding.UTF8.GetString(bytes), string s => s, _ => value.ToString() }
            : null;

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
