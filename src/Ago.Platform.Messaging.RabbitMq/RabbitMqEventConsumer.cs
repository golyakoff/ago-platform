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
    public Task SubscribeAsync(
        string topic,
        SubscriptionMode mode,
        string consumerName,
        RetryPolicy retryPolicy,
        Func<EventEnvelope, IMessageContext, CancellationToken, Task> handler,
        CancellationToken cancellationToken) =>
        SubscribeAsync(topic, mode, consumerName, retryPolicy, QueueLifetime.Durable, handler, cancellationToken);

    public async Task SubscribeAsync(
        string topic,
        SubscriptionMode mode,
        string consumerName,
        RetryPolicy retryPolicy,
        QueueLifetime queueLifetime,
        Func<EventEnvelope, IMessageContext, CancellationToken, Task> handler,
        CancellationToken cancellationToken)
    {
        var channel = await connection.CreateChannelAsync(cancellationToken: cancellationToken);
        await channel.BasicQosAsync(0, prefetchCount: 50, global: false, cancellationToken);

        await channel.ExchangeDeclareAsync(topic, ExchangeType.Fanout, durable: true, cancellationToken: cancellationToken);

        var queueName = mode == SubscriptionMode.Competing ? $"{topic}.{consumerName}" : $"{topic}.{Guid.NewGuid():N}";

        // `15-15`: a queue tied to this one connection's lifetime, not just Broadcast's own reason for
        // being exclusive+auto-delete (a fresh, randomly-named queue every subscribe call has nothing
        // else that could ever reattach to it) but also a Competing subscription whose caller has said,
        // via QueueLifetime.ProcessScoped, that its consumer name already names something with no life
        // beyond this process. Every other Competing subscription (the overwhelming majority - every
        // durable topic in messaging.md's table) is unaffected: exclusive/autoDelete stay false exactly
        // as before this item, because QueueLifetime.Durable is what the six-argument overload above
        // forwards here.
        var ephemeral = mode == SubscriptionMode.Broadcast || queueLifetime == QueueLifetime.ProcessScoped;
        await channel.QueueDeclareAsync(
            queue: queueName, durable: !ephemeral, exclusive: ephemeral, autoDelete: ephemeral,
            cancellationToken: cancellationToken);
        await channel.QueueBindAsync(queueName, topic, routingKey: string.Empty, cancellationToken: cancellationToken);

        // The retry queue exists only to serve this queue's own redelivery loop - it has no life
        // independent of the queue above, so it shares that queue's lifetime rather than being
        // unconditionally durable. For a Durable subscription this is exactly the pre-15-15 shape
        // (always durable, never auto-deleted); for a ProcessScoped one, a retry queue that outlived
        // its own main queue would be exactly the orphan this item exists to stop leaking.
        var retryQueueName = $"{queueName}.retry";
        await channel.QueueDeclareAsync(
            queue: retryQueueName, durable: !ephemeral, exclusive: ephemeral, autoDelete: ephemeral,
            arguments: new Dictionary<string, object?>
            {
                ["x-message-ttl"] = (int)retryPolicy.InitialBackoff.TotalMilliseconds,
                ["x-dead-letter-exchange"] = "",
                ["x-dead-letter-routing-key"] = queueName,
            },
            cancellationToken: cancellationToken);

        // The dead-letter queue deliberately does NOT follow queueLifetime, unlike the two queues
        // above - it is not owned by this one subscription the way its own retry queue is.
        // `Broadcast_TwoConsumers_BothReceiveEveryMessage` (pre-existing, unrelated to `15-15`) already
        // proves a DLQ name can legitimately be shared across two independent subscriptions on the
        // same topic; making it exclusive here broke that test with a real RESOURCE_LOCKED from the
        // broker the moment a second subscription tried to declare the same name - caught only by
        // running the full suite, not by this item's own new tests, which is exactly why the full
        // suite (not just new tests) is the bar. A DLQ is a monitored, durable destination for poison
        // messages regardless of which consumer instance produced them (messaging.md: "a DLQ with no
        // alert and no runbook entry is a silent data-loss channel") - that job does not change when
        // the consumer that fills it happens to be ProcessScoped.
        //
        // `NodeDeliveryConsumer` (this item's motivating ProcessScoped caller) still gets the orphan
        // problem solved: it never actually dead-letters (MaxAttempts: 1, and its handler acks even on
        // failure), so its own fix is naming its DLQ once, shared across every node, rather than
        // per-pod (`NodeDeliveryConsumer`'s own remarks) - a durable queue that exists exactly once
        // regardless of restarts, not the orphan this item measured.
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
