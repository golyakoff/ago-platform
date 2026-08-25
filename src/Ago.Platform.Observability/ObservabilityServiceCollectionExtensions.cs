using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Ago.Platform.Observability;

/// <summary>
/// `7-09`: named <c>ObservabilityServiceCollectionExtensions</c>, not <c>ServiceCollectionExtensions</c>,
/// because <c>Ago.Platform.Hosting</c> already has a class by that name and a host referencing both
/// packages would otherwise hit CS0433 on every call site. The rename is the fix; a <c>using</c> alias
/// would only hide the collision at each call site instead of removing it.
/// </summary>
public static class ObservabilityServiceCollectionExtensions
{
    /// <summary>
    /// `7-01`/`7-02`: one call from the <c>Program.cs</c> of every **ASP.NET Core** host that wants
    /// telemetry - wires both OTel signals this platform ships, tracing (`7-01`) and metrics (`7-02`):
    /// ASP.NET Core, HttpClient and Npgsql instrumentation (tracing only - Npgsql has no
    /// metrics instrumentation to add), resource attributes that make every host's telemetry
    /// distinguishable in one Jaeger/Prometheus pair, an OTLP exporter for traces pointed at
    /// <c>Otel:Exporter:Endpoint</c> (Jaeger), and a Prometheus scrape endpoint for metrics - the two
    /// signals use different transports deliberately (Jaeger's OTLP receiver only implements the trace
    /// collector service; Prometheus's own model is pull, not push - a `7-02` mistake found and fixed
    /// live while verifying `7-03`, see the metrics builder below for the full story).
    ///
    /// **This method requires a web host** and says so rather than pretending otherwise (`7-09`,
    /// adr/0046): <c>AddAspNetCoreInstrumentation()</c>
    /// listens to ASP.NET Core's own diagnostic source, and <c>AddPrometheusExporter()</c> is only half
    /// a scrape endpoint - the other half is <c>app.MapPrometheusScrapingEndpoint()</c> on an
    /// <c>IEndpointRouteBuilder</c>, which a <c>Microsoft.NET.Sdk.Worker</c> generic host does not have.
    /// Calling it from a generic host compiles and starts, and then exports metrics nothing can ever
    /// scrape. A generic-host-shaped entry point is a real gap, named in adr/0046's Consequences and
    /// deliberately not guessed at here from zero callers - the split this project *is* means it can be
    /// added later without touching <c>Ago.Platform.Hosting</c> at all.
    ///
    /// This is deliberately the platform/product seam clean-architecture.md describes: it can wire
    /// *generic* OTel SDK instrumentation (nothing here has ever heard of a conversation, a visitor or
    /// a booking) but cannot start the *manual* spans/instruments a product's own hub methods,
    /// pipeline, outbox dispatcher or consumers need - those live in the product (or in a platform
    /// adapter like `Ago.Platform.Messaging.RabbitMq`/`Ago.Platform.Resilience`/
    /// `Ago.Platform.Caching.Redis`/`Ago.Platform.Realtime`) against their own
    /// <c>ActivitySource</c>/<c>Meter</c>, picked up here only through
    /// <see cref="ActivitySourceWildcard"/>/<see cref="MeterWildcard"/>.
    ///
    /// `7-02`'s own judgment call: metrics were folded into this existing method rather than added as
    /// a sibling `AddPlatformMetrics` - both signals configure the same OTel SDK builder
    /// (`ConfigureResource` -> `WithTracing`/`WithMetrics`), so a second call would either duplicate
    /// the resource/options setup above or split it awkwardly across two methods, and every host
    /// already calls this one method exactly once from its own `Program.cs` (`7-01`'s own invariant) -
    /// two calls to remember instead of one is a foot-gun (wire tracing, forget metrics) with no
    /// offsetting benefit, since nothing here needs the two signals configured independently.
    ///
    /// Npgsql needs no instrumentation *package* at all: it has emitted its own Activities on an
    /// ActivitySource named "Npgsql" since Npgsql 6.0, gated behind whether anything is listening -
    /// <c>.AddSource("Npgsql")</c> is the whole integration, which is also why this method takes no
    /// dependency on Npgsql itself (naming-and-structure.md: this project stays a thin, generic
    /// bootstrap library, never referencing a specific database driver). Npgsql does not publish a
    /// matching metrics source, so <see cref="MeterWildcard"/>'s subscription below has no Npgsql
    /// equivalent to add - a metrics gap this item does not invent a workaround for.
    /// </summary>
    public static IServiceCollection AddPlatformObservability(
        this IServiceCollection services, IConfiguration configuration, string serviceName)
    {
        services
            .AddOptions<PlatformObservabilityOptions>()
            .Bind(configuration.GetSection(PlatformObservabilityOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // The OTel SDK's builder API (ConfigureResource/WithTracing/WithMetrics below) configures
        // both pipelines once, synchronously, right here - not lazily from a resolved IOptions<T> the
        // way ordinary request-time configuration would. Binding a second, throwaway instance and
        // validating it eagerly is what turns a bad Otel:* value into a startup failure immediately
        // (before anything is exported to a wrong or missing endpoint) rather than only once
        // ValidateOnStart's own deferred host-startup check runs - the AddOptions<T> registration
        // above still exists too, so a *test* can exercise the same standard IOptions<T> validation
        // pipeline every other options class in this codebase is tested through.
        var options = configuration.GetSection(PlatformObservabilityOptions.SectionName).Get<PlatformObservabilityOptions>()
            ?? new PlatformObservabilityOptions();
        Validator.ValidateObject(options, new ValidationContext(options), validateAllProperties: true);

        services.AddOpenTelemetry()
            .ConfigureResource(resource => resource
                .AddService(serviceName, serviceVersion: options.ServiceVersion)
                .AddAttributes([new KeyValuePair<string, object>("deployment.environment", options.DeploymentEnvironment)]))
            .WithTracing(tracing => tracing
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddSource("Npgsql")
                // Every Ago.* manual ActivitySource (a product's own hub/outbox/consumer spans,
                // Ago.Platform.Realtime's/Ago.Platform.Messaging.RabbitMq's generic propagation
                // spans) in one wildcard subscription - the OTel SDK's own supported wildcard match,
                // not a per-source list this project would otherwise need updating every time a
                // product adds a new manually-instrumented class. "Ago.*" rather than naming any
                // product specifically is the actual platform/product boundary point: this project
                // has no access to those names (they are different repositories) and must not need
                // one - which is exactly what lets a second product (Ago.Calendar.*) be instrumented
                // by the same wildcard with no change here.
                .AddSource(ActivitySourceWildcard)
                .AddOtlpExporter(otlp => otlp.Endpoint = options.Exporter.Endpoint!))
            .WithMetrics(metrics => metrics
                // Both packages already referenced for tracing above double as the metrics
                // instrumentation for the same two signals (RED numbers per endpoint/outbound HTTP
                // call) - one package, one AddXInstrumentation() call per signal builder, nothing new
                // to reference (naming-and-structure.md's own "no dependency without saying what it
                // replaces" - this replaces nothing, it is the same package's second extension method).
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                // The metrics mirror of ActivitySourceWildcard above - every Ago.* manual Meter
                // (a product's hub/pipeline/outbox/assignment instruments, Ago.Platform.Resilience's
                // breaker/bulkhead instruments, Ago.Platform.Caching.Redis's hit-ratio counter,
                // Ago.Platform.Realtime's connections gauge, Ago.Platform.Messaging.RabbitMq's
                // consumer RED/DLQ counters) in one wildcard subscription, the OTel .NET SDK's own
                // supported wildcard match for AddMeter, mirroring AddSource's.
                .AddMeter(MeterWildcard)
                // `7-02` fix (found live while verifying `7-03`): metrics do NOT get the same
                // AddOtlpExporter(...) tracing uses above. Prometheus's own model is pull/scrape, not
                // push - and the OTLP push this originally shipped with pointed at the same
                // Otel:Exporter:Endpoint as tracing (Jaeger), which does not implement the OTLP
                // *metrics* collector service at all, so every metric silently went nowhere. Confirmed
                // live: a running host's own /metrics returned a bare 404 before this fix, and
                // Prometheus's targets page showed every target DOWN with a real connection
                // failure, not a wiring mistake in `7-03`'s own scrape config. AddPrometheusExporter()
                // is what actually gives Prometheus something to scrape - `7-03`'s own backlog scope
                // ("Prometheus scrape config targeting each host's /metrics endpoint") already named
                // pull as the intended model, this just makes the code match it.
                .AddPrometheusExporter());

        return services;
    }

    /// <summary>See <see cref="AddPlatformObservability"/>'s own remarks - every product's manual
    /// <c>ActivitySource</c> name is expected to start with "Ago." so this one wildcard subscription
    /// covers all of them without this project ever naming one.</summary>
    public const string ActivitySourceWildcard = "Ago.*";

    /// <summary>`7-02`: the metrics counterpart to <see cref="ActivitySourceWildcard"/> - every
    /// manually-instrumented <c>Meter</c> this platform or a product hosts is expected to start with
    /// "Ago." so this one wildcard subscription covers all of them without this project ever naming
    /// one.</summary>
    public const string MeterWildcard = "Ago.*";
}
