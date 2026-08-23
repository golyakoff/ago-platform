using Ago.Platform.Abstractions;
using Ago.Platform.Resilience;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Polly;
using StackExchange.Redis;

namespace Ago.Platform.Caching.Redis;

public static class ServiceCollectionExtensions
{
    // The name this module's pipeline is bound and registered under - Resilience:Redis:* in
    // configuration, and the key AddResiliencePipelineOptions's named IOptionsMonitor is read back
    // with below.
    private const string PipelineName = "Redis";

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

        services.AddResiliencePipelineOptions(PipelineName, configuration, ConfigureDefaults);
        services.AddSingleton(BuildResiliencePipeline);
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

    // Fixed historical values - what used to be hardcoded literals inside this project's own private
    // BuildResiliencePipeline() before 6-01 extracted Ago.Platform.Resilience. resilience.md asks for
    // "short timeout, circuit breaker, fallback to cache miss," not a tuned number - these stay
    // deliberately conservative and unmeasured (CLAUDE.md: "do not invent numbers... measure or stay
    // silent"), unchanged by the extraction itself, a placeholder to be revisited once Stage 7's load
    // test has a real Redis-latency distribution to tune against, not before. Resilience:Redis:* in
    // configuration overrides any of them; nothing here binds a Retry or Bulkhead group, so
    // BuildResiliencePipeline below never calls WithRetry/WithBulkhead - Redis's row in resilience.md
    // does not call for either.
    private static void ConfigureDefaults(ResiliencePipelineOptions options)
    {
        options.Timeout = new ResilienceTimeoutOptions { Duration = TimeSpan.FromMilliseconds(200) };
        options.CircuitBreaker = new ResilienceCircuitBreakerOptions
        {
            FailureRatio = 0.5,
            MinimumThroughput = 4,
            SamplingDuration = TimeSpan.FromSeconds(10),
            BreakDuration = TimeSpan.FromSeconds(5),
        };
    }

    private static ResiliencePipeline BuildResiliencePipeline(IServiceProvider sp)
    {
        var options = sp.GetRequiredService<IOptionsMonitor<ResiliencePipelineOptions>>().Get(PipelineName);
        return new ResiliencePolicyBuilder()
            .WithTimeout(options.Timeout!)
            .WithCircuitBreaker(options.CircuitBreaker!, static ex => ex is not OperationCanceledException)
            .Build();
    }
}
