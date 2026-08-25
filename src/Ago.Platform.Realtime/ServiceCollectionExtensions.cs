using Ago.Platform.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StackExchange.Redis;

namespace Ago.Platform.Realtime;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddConnectionRegistry(this IServiceCollection services, IConfiguration configuration)
    {
        // TryAdd, not Add: Caching.Redis (3-04) registers the same way against the same
        // Redis:ConnectionString key, so whichever of the two a host wires up first wins and the
        // other reuses the one connection rather than opening a second (naming-and-structure.md:
        // "one project per external technology" - one physical connection, two adapter projects).
        services.TryAddSingleton<IConnectionMultiplexer>(_ =>
        {
            var connectionString = configuration["Redis:ConnectionString"]
                ?? throw new InvalidOperationException("Set Redis:ConnectionString.");
            return ConnectionMultiplexer.Connect(connectionString);
        });

        services
            .AddOptions<ConnectionRegistryOptions>()
            .Bind(configuration.GetSection(ConnectionRegistryOptions.SectionName))
            .ValidateOnStart();
        services
            .AddOptions<ConnectionHeartbeatOptions>()
            .Bind(configuration.GetSection(ConnectionHeartbeatOptions.SectionName))
            .ValidateOnStart();
        // 3-06: registered here (not left to the host) for the same reason DrainState needs no
        // per-host wiring - the host still decides whether to run ConnectionDrainCoordinator itself
        // (only Ago.Chat.Api does), but the options/state it needs exist regardless.
        services
            .AddOptions<DrainOptions>()
            .Bind(configuration.GetSection(DrainOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<DrainState>();

        // Every generic AddSingleton/TryAddSingleton overload requires TService : class - NodeId
        // is a struct (naming-and-structure.md/existing convention: strongly-typed ids are
        // readonly record structs throughout this codebase, and changing that for DI's convenience
        // was not worth it). The non-generic, Type-based overload has no such constraint.
        services.AddSingleton(typeof(NodeId), _ => (object)ResolveNodeId());
        // `7-07`: the tracker is also the connections gauge's source, and this is the only place
        // that knows both which tracker this host has and which node it is - so the metric is wired
        // here, in the composition root, rather than by the tracker reaching for a static or by a
        // counter maintained inside RedisConnectionRegistry (which is what shipped in `7-02` and
        // counted heartbeat refreshes as connections).
        services.AddSingleton(serviceProvider =>
        {
            var tracker = new LocalConnectionTracker();
            RealtimeMetrics.TrackNode(serviceProvider.GetRequiredService<NodeId>(), tracker);
            return tracker;
        });
        services.AddSingleton<IConnectionRegistry, RedisConnectionRegistry>();
        // Depends on IEventPublisher/IClock being registered by the host's own composition root
        // (AddRabbitMqMessaging, AddPlatformKernel) - not this method's concern to register them.
        services.AddSingleton<INodeFanoutPublisher, NodeFanoutPublisher>();

        return services;
    }

    /// <summary>Kubernetes sets <c>HOSTNAME</c> to the pod name automatically - stable for the
    /// pod's lifetime, exactly what realtime.md's "who is connected where" needs. Outside a pod
    /// (local dev, a test host), a random suffix on the machine name keeps two processes on one
    /// machine from colliding.</summary>
    private static NodeId ResolveNodeId() =>
        new(Environment.GetEnvironmentVariable("HOSTNAME") ?? $"{Environment.MachineName}-{Guid.NewGuid():N}");
}
