using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Ago.Platform.Storage.S3;

/// <summary>
/// Lives here, not a product's own project - the same `PersistenceBoundaryTests`-style reasoning
/// `RedisHealthCheck` already states: a product using `IFileStorage` never sees `IAmazonS3` itself,
/// and a health check that talks to storage directly belongs on this side of that boundary.
/// </summary>
public sealed class S3HealthCheck(IAmazonS3 client, S3StorageOptions options) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await client.GetBucketLocationAsync(new GetBucketLocationRequest { BucketName = options.Bucket }, cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Cannot reach S3/MinIO.", ex);
        }
    }
}
