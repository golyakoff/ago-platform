using Ago.Platform.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Polly;
using Polly.CircuitBreaker;
using Polly.Timeout;
using StackExchange.Redis;

namespace Ago.Platform.Caching.Redis;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddRedisCaching(this IServiceCollection services, IConfiguration configuration)
    {
        // TryAdd, not Add: Ago.Platform.Realtime's AddConnectionRegistry registers the same way
        // against the same Redis:ConnectionString key (its own comment already anticipates this) -
        // whichever of the two a host wires up first wins, and the other reuses one physical
        // connection rather than opening a second (naming-and-structure.md: "one project per external
        // technology" - one connection, two adapter projects).
        services.TryAddSingleton<IConnectionMultiplexer>(_ =>
        {
            var connectionString = configuration["Redis:ConnectionString"]
                ?? throw new InvalidOperationException("Set Redis:ConnectionString.");
            return ConnectionMultiplexer.Connect(connectionString);
        });

        services.AddSingleton(BuildResiliencePipeline());
        services.AddSingleton<ICache, RedisCache>();
        services.AddSingleton<CacheInvalidationPublisher>();
        // 3-05: same technology, same project (naming-and-structure.md's "one project per external
        // technology") - reuses the IConnectionMultiplexer/ResiliencePipeline registrations above
        // rather than opening a second connection or building a second resilience pipeline.
        services.AddSingleton<IRateLimiter, RedisRateLimiter>();
        // 4-03: same technology, same project, same resilience pipeline - reuses the registrations
        // above rather than opening a second connection.
        services.AddSingleton<RedisDistributedLock>();

        return services;
    }

    // Fixed, not configurable: resilience.md asks for "short timeout, circuit breaker, fallback to
    // cache miss," not a tuned number - these values are deliberately conservative and unmeasured
    // (CLAUDE.md: "do not invent numbers... measure or stay silent"), a placeholder to be revisited
    // once Stage 7's load test has a real Redis-latency distribution to tune against, not before.
    private static ResiliencePipeline BuildResiliencePipeline() =>
        new ResiliencePipelineBuilder()
            .AddTimeout(TimeSpan.FromMilliseconds(200))
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions
            {
                FailureRatio = 0.5,
                MinimumThroughput = 4,
                SamplingDuration = TimeSpan.FromSeconds(10),
                BreakDuration = TimeSpan.FromSeconds(5),
                ShouldHandle = new PredicateBuilder().Handle<Exception>(ex => ex is not OperationCanceledException),
            })
            .Build();
}
