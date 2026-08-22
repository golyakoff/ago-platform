using Ago.Platform.Abstractions;
using Ago.Platform.Storage.S3;
using Microsoft.Extensions.Logging.Abstractions;
using Polly;
using Polly.Retry;
using Testcontainers.Minio;

namespace Ago.Platform.Integration.Tests;

/// <summary>
/// `5-02`'s Done-when: unlike `ICache` (always degrades to a miss) or `RedisDistributedLock` (fails
/// closed, returns null), `IFileStorage` has no sensible default for "storage is unreachable" - it
/// must throw <see cref="FileStorageUnavailableException"/>, never a raw AWS SDK exception and never
/// a silent success. Its own, non-shared container so stopping it cannot affect any other test.
/// </summary>
public sealed class S3FileStorageContainerFailureTests
{
    [Fact]
    public async Task GetMetadataAsync_AgainstAStoppedMinio_ThrowsFileStorageUnavailableException()
    {
        const string username = "ago-test";
        const string password = "ago-test-local-dev";
        var container = new MinioBuilder("minio/minio:RELEASE.2025-09-07T16-13-09Z").WithUsername(username).WithPassword(password).Build();
        await container.StartAsync();

        var options = new S3StorageOptions
        {
            ServiceUrl = container.GetConnectionString(),
            AccessKey = container.GetAccessKey(),
            SecretKey = container.GetSecretKey(),
            Bucket = "attachments",
            ForcePathStyle = true,
        };
        using var client = S3ClientFactory.Create(options);

        // A short pipeline (not the production defaults) so a genuinely unreachable MinIO resolves
        // quickly here rather than the test waiting out a multi-second retry/circuit-breaker budget.
        var resilience = new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions { MaxRetryAttempts = 1, Delay = TimeSpan.FromMilliseconds(50) })
            .AddTimeout(TimeSpan.FromMilliseconds(500))
            .Build();
        var storage = new S3FileStorage(client, options, resilience, NullLogger<S3FileStorage>.Instance);

        await container.StopAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var exception = await Record.ExceptionAsync(
            () => storage.GetMetadataAsync(new ObjectKey("test/whatever.txt"), cts.Token));

        Assert.IsType<FileStorageUnavailableException>(exception);
        Assert.False(cts.IsCancellationRequested, "Should have failed on its own, not because the test's timeout fired.");

        await container.DisposeAsync();
    }
}
