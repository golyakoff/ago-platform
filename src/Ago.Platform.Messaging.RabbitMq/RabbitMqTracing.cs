using System.Diagnostics;

namespace Ago.Platform.Messaging.RabbitMq;

/// <summary>
/// `7-01`: messaging.md's "trace context propagation through... the broker" - the trace id captured
/// at write time must survive the poll-and-publish handoff as a real child span, not a same-time
/// coincidence. This adapter is the one place that can do it: <see cref="RabbitMqEventPublisher"/>
/// injects the W3C `traceparent` of whatever <see cref="Activity"/> is current at the moment it
/// publishes (the caller - `Ago.Chat.Worker`'s outbox dispatcher - is expected to have already
/// started one, parented from the trace captured at write time; this project has no idea what an
/// outbox row is, only that "whatever is current" is worth carrying across), and
/// <see cref="RabbitMqEventConsumer"/> extracts it back into a real parent
/// <see cref="ActivityContext"/> for the handler it invokes. Neither <see cref="EventEnvelope"/> nor
/// either port in `Ago.Platform.Abstractions` changes for this - propagation is transport-level
/// plumbing entirely inside the adapter, the same "the abstraction stays topic + partition key +
/// at-least-once, nothing broker-specific leaks through it" discipline `adr/0006` already states for
/// everything else this adapter does with headers.
/// </summary>
internal static class RabbitMqTracing
{
    internal const string TraceParentHeader = "traceparent";

    /// <summary>
    /// This adapter's own `ActivitySource`, generic on purpose (`Ago.Platform.Messaging.RabbitMq`,
    /// not a product name) - it wraps *every* consumer's handler invocation, for every product this
    /// platform ever hosts, which is exactly why the span it starts is named after the topic being
    /// consumed rather than anything chat-specific.
    /// `Ago.Platform.Observability.AddPlatformObservability` picks it up through the "Ago.*"
    /// wildcard, never by this literal name.
    /// </summary>
    internal static readonly ActivitySource Source = new("Ago.Platform.Messaging.RabbitMq");

    /// <summary>Best-effort: an invalid or absent header means no parent, not a startup failure - a
    /// message published before this shipped, or by a future non-.NET producer, still gets consumed
    /// and processed correctly, just without a linked trace.</summary>
    internal static bool TryParseTraceParent(string? traceParent, out ActivityContext context)
    {
        if (string.IsNullOrEmpty(traceParent))
        {
            context = default;
            return false;
        }

        return ActivityContext.TryParse(traceParent, traceState: null, out context);
    }
}
