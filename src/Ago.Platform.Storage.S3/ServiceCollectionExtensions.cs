using Ago.Platform.Abstractions;
using Ago.Platform.Resilience;
using Amazon.S3;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Polly;

namespace Ago.Platform.Storage.S3;

public static class ServiceCollectionExtensions
{
    // The name this module's pipeline is bound and registered under - Resilience:S3:* in
    // configuration, and the key AddResiliencePipelineOptions's named IOptionsMonitor is read back
    // with below.
    private const string PipelineName = "S3";

    public static IServiceCollection AddS3FileStorage(this IServiceCollection services, IConfiguration configuration)
    {
        services
            .AddOptions<S3StorageOptions>()
            .Bind(configuration.GetSection(S3StorageOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<S3StorageOptions>>().Value);

        services.AddSingleton(sp => S3ClientFactory.Create(sp.GetRequiredService<S3StorageOptions>()));

        services.AddResiliencePipelineOptions(PipelineName, configuration, ConfigureDefaults);
        services.AddSingleton(BuildResiliencePipeline);
        services.AddSingleton<IFileStorage, S3FileStorage>();

        return services;
    }

    // Fixed historical values - what used to be hardcoded literals inside this project's own private
    // BuildResiliencePipeline() before 6-01 extracted Ago.Platform.Resilience, unchanged by the
    // extraction itself (resilience.md's S3/MinIO row: "timeout, retry, circuit breaker on the
    // presign path"; unmeasured starting points per CLAUDE.md). A 404 (an object genuinely does not
    // exist) is excluded from both retry and breaker via IsTransient below, since retrying or
    // breaking on an expected outcome only slows down a normal answer (S3FileStorage.GetMetadataAsync's
    // own remarks). Resilience:S3:* in configuration overrides any of them; nothing here binds a
    // Bulkhead group - S3's row in resilience.md does not call for one.
    private static void ConfigureDefaults(ResiliencePipelineOptions options)
    {
        options.Retry = new ResilienceRetryOptions
        {
            MaxRetryAttempts = 3,
            BackoffType = DelayBackoffType.Exponential,
            Delay = TimeSpan.FromMilliseconds(100),
        };
        options.Timeout = new ResilienceTimeoutOptions { Duration = TimeSpan.FromSeconds(5) };
        options.CircuitBreaker = new ResilienceCircuitBreakerOptions
        {
            FailureRatio = 0.5,
            MinimumThroughput = 4,
            SamplingDuration = TimeSpan.FromSeconds(30),
            BreakDuration = TimeSpan.FromSeconds(10),
        };
    }

    private static ResiliencePipeline BuildResiliencePipeline(IServiceProvider sp)
    {
        var options = sp.GetRequiredService<IOptionsMonitor<ResiliencePipelineOptions>>().Get(PipelineName);
        return new ResiliencePolicyBuilder(PipelineName)
            .WithRetry(options.Retry!, IsTransient)
            .WithTimeout(options.Timeout!)
            .WithCircuitBreaker(options.CircuitBreaker!, IsTransient)
            .Build();
    }

    private static bool IsTransient(Exception ex) =>
        ex is not OperationCanceledException &&
        (ex is not AmazonS3Exception s3 || (int)s3.StatusCode >= 500 || s3.StatusCode == 0);
}
