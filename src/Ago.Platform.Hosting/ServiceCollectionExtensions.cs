using System.ComponentModel.DataAnnotations;
using Ago.Platform.Kernel;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Ago.Platform.Hosting;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <c>Kernel</c>'s primitives with their default, real implementations -
    /// <see cref="SystemClock"/> for <see cref="IClock"/>, <see cref="UuidV7Generator"/> for
    /// <see cref="IIdGenerator"/>. Both are stateless and thread-safe, hence singleton.
    /// </summary>
    public static IServiceCollection AddPlatformKernel(this IServiceCollection services)
    {
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<IIdGenerator, UuidV7Generator>();
        return services;
    }

    /// <summary>
    /// `7-01`: one call from every host's <c>Program.cs</c> (`Ago.Chat.Api`/`Worker`/`Webhooks`) -
    /// wires the OTel SDK's tracing provider with ASP.NET Core, HttpClient and Npgsql
    /// instrumentation, resource attributes that make every host's spans distinguishable in one
    /// Jaeger UI, and an OTLP exporter pointed at <c>Otel:Exporter:Endpoint</c>. This is deliberately
    /// the platform/product seam clean-architecture.md describes for `Ago.Platform.Hosting` itself:
    /// it can wire *generic* OTel SDK instrumentation (nothing here has ever heard of a
    /// conversation or a visitor) but cannot start the *manual* spans a product's own hub methods,
    /// outbox dispatcher or consumers need - those live in `Ago.Chat.*` against their own
    /// `ActivitySource`, picked up here only through <see cref="ActivitySourceWildcard"/>.
    ///
    /// Npgsql needs no instrumentation *package* at all: it has emitted its own Activities on an
    /// ActivitySource named "Npgsql" since Npgsql 6.0, gated behind whether anything is listening -
    /// <c>.AddSource("Npgsql")</c> is the whole integration, which is also why this method takes no
    /// dependency on Npgsql itself (naming-and-structure.md: this project stays a thin, generic
    /// bootstrap library, never referencing a specific database driver).
    /// </summary>
    public static IServiceCollection AddPlatformObservability(
        this IServiceCollection services, IConfiguration configuration, string serviceName)
    {
        services
            .AddOptions<PlatformObservabilityOptions>()
            .Bind(configuration.GetSection(PlatformObservabilityOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // The OTel SDK's builder API (ConfigureResource/WithTracing below) configures the tracing
        // pipeline once, synchronously, right here - not lazily from a resolved IOptions<T> the way
        // ordinary request-time configuration would. Binding a second, throwaway instance and
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
                // Every Ago.* manual ActivitySource (Ago.Chat's own hub/outbox/consumer spans,
                // Ago.Platform.Realtime's/Ago.Platform.Messaging.RabbitMq's generic propagation
                // spans) in one wildcard subscription - the OTel SDK's own supported wildcard match,
                // not a per-source list this project would otherwise need updating every time a
                // product adds a new manually-instrumented class. "Ago.*" rather than naming
                // "Ago.Chat" specifically is the actual platform/product boundary point: this project
                // has no access to that name (it is a different repository) and must not need one.
                .AddSource(ActivitySourceWildcard)
                .AddOtlpExporter(otlp => otlp.Endpoint = options.Exporter.Endpoint!));

        return services;
    }

    /// <summary>See <see cref="AddPlatformObservability"/>'s own remarks - every product's manual
    /// <c>ActivitySource</c> name is expected to start with "Ago." so this one wildcard subscription
    /// covers all of them without this project ever naming one.</summary>
    public const string ActivitySourceWildcard = "Ago.*";
}
