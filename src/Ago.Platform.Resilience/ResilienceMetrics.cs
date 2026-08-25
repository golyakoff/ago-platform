using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Polly.CircuitBreaker;

namespace Ago.Platform.Resilience;

/// <summary>
/// `7-02`: nfr.md's "breaker state" and "bulkhead rejections... per named resilience pipeline" -
/// instrumented here, inside the shared pipeline builder itself
/// (<see cref="ResiliencePolicyBuilder.WithCircuitBreaker"/>/<see cref="ResiliencePolicyBuilder.WithBulkhead"/>),
/// not once per boundary (`Ago.Platform.Caching.Redis`, `Ago.Platform.Storage.S3`, and `6-05`'s
/// webhook dispatcher) - the same "add the instrument to the shared builder, not a new pipeline per
/// caller" placement this item's backlog names explicitly for this package.
///
/// A plain dependency-free static holder, the metrics twin of how a product's own tracing holder
/// keeps one shared <c>ActivitySource</c> - public for the same reason those are: the instrument
/// names below are the reviewer-facing contract `7-03`'s Grafana panels are built against, not a
/// private implementation detail. Every named pipeline built anywhere in this codebase reports
/// through this one <see cref="Meter"/>, so `7-01`'s "Ago.*" wildcard subscription
/// (<c>Ago.Platform.Observability.ObservabilityServiceCollectionExtensions.MeterWildcard</c>) picks
/// it up without this project needing to know that subscription exists - and, since the instrument
/// is a BCL <see cref="Meter"/>, without this project referencing the OpenTelemetry SDK at all
/// (`7-09`/adr/0046).
/// </summary>
public static class ResilienceMetrics
{
    public const string MeterName = "Ago.Platform.Resilience";

    public const string CircuitBreakerStateInstrumentName = "ago.platform.resilience.circuit_breaker.state";

    public const string BulkheadRejectionsInstrumentName = "ago.platform.resilience.bulkhead.rejections";

    internal static readonly Meter Meter = new(MeterName);

    // Keyed by pipeline name ("Redis", "S3", a future "Webhooks") - Polly's own
    // CircuitBreakerStateProvider is a live handle into the strategy's current state, not a snapshot,
    // so the gauge callback below always reads whatever state the breaker is actually in right now,
    // not a value this class tracks itself via OnOpened/OnClosed callbacks (which would just be a
    // slower, more error-prone way to reach the same live value Polly already exposes).
    private static readonly ConcurrentDictionary<string, CircuitBreakerStateProvider> BreakerStates = new();

    private static readonly Counter<long> BulkheadRejectionsCounter = Meter.CreateCounter<long>(
        BulkheadRejectionsInstrumentName,
        unit: "{rejection}",
        description: "Executions rejected by a named pipeline's bulkhead (concurrency limit and queue both exhausted), tagged by pipeline.");

    private static readonly CircuitState[] AllStates =
        [CircuitState.Closed, CircuitState.Open, CircuitState.HalfOpen, CircuitState.Isolated];

    static ResilienceMetrics()
    {
        // One state per named pipeline, one measurement per (pipeline, state) pair every collection -
        // the "state as a 0/1-valued label" shape (rather than a single numeric-enum gauge) is the
        // standard OTel/Prometheus idiom for an enum-like value: a dashboard can sum by state without
        // the reader needing to know Closed=0/Open=1/HalfOpen=2/Isolated=3 means anything ordinally.
        Meter.CreateObservableGauge(
            CircuitBreakerStateInstrumentName,
            ObserveBreakerStates,
            unit: "{state}",
            description: "1 for a named pipeline's current circuit-breaker state, 0 for every other state; tagged by pipeline and state.");
    }

    internal static void RegisterBreaker(string pipelineName, CircuitBreakerStateProvider stateProvider) =>
        BreakerStates[pipelineName] = stateProvider;

    internal static void RecordBulkheadRejection(string pipelineName) =>
        BulkheadRejectionsCounter.Add(1, new KeyValuePair<string, object?>("pipeline", pipelineName));

    private static IEnumerable<Measurement<int>> ObserveBreakerStates()
    {
        foreach (var (pipeline, stateProvider) in BreakerStates)
        {
            var current = stateProvider.CircuitState;
            foreach (var state in AllStates)
            {
                yield return new Measurement<int>(
                    state == current ? 1 : 0,
                    new KeyValuePair<string, object?>("pipeline", pipeline),
                    new KeyValuePair<string, object?>("state", StateTag(state)));
            }
        }
    }

    private static string StateTag(CircuitState state) => state switch
    {
        CircuitState.Closed => "closed",
        CircuitState.Open => "open",
        CircuitState.HalfOpen => "half_open",
        CircuitState.Isolated => "isolated",
        _ => "unknown",
    };
}
