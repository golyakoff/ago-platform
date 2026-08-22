using Ago.Platform.Abstractions;
using Amazon.S3;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;

namespace Ago.Platform.Storage.S3;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddS3FileStorage(this IServiceCollection services, IConfiguration configuration)
    {
        services
            .AddOptions<S3StorageOptions>()
            .Bind(configuration.GetSection(S3StorageOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<S3StorageOptions>>().Value);

        services.AddSingleton(sp => S3ClientFactory.Create(sp.GetRequiredService<S3StorageOptions>()));

        services.AddSingleton(BuildResiliencePipeline());
        services.AddSingleton<IFileStorage, S3FileStorage>();

        return services;
    }

    // Retry+timeout+circuit-breaker (resilience.md's S3/MinIO row), unmeasured starting points
    // (CLAUDE.md) - a 404 (an object genuinely does not exist) is excluded from both the retry and
    // the breaker's failure count, since retrying or breaking on an expected outcome only slows down
    // a normal answer (S3FileStorage.GetMetadataAsync's own remarks).
    private static ResiliencePipeline BuildResiliencePipeline() =>
        new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts = 3,
                BackoffType = DelayBackoffType.Exponential,
                Delay = TimeSpan.FromMilliseconds(100),
                ShouldHandle = new PredicateBuilder().Handle<Exception>(IsTransient),
            })
            .AddTimeout(TimeSpan.FromSeconds(5))
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions
            {
                FailureRatio = 0.5,
                MinimumThroughput = 4,
                SamplingDuration = TimeSpan.FromSeconds(30),
                BreakDuration = TimeSpan.FromSeconds(10),
                ShouldHandle = new PredicateBuilder().Handle<Exception>(IsTransient),
            })
            .Build();

    private static bool IsTransient(Exception ex) =>
        ex is not OperationCanceledException &&
        (ex is not AmazonS3Exception s3 || (int)s3.StatusCode >= 500 || s3.StatusCode == 0);
}
