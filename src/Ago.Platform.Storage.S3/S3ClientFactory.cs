using Amazon;
using Amazon.Runtime;
using Amazon.S3;

namespace Ago.Platform.Storage.S3;

/// <summary>
/// One place builds <see cref="IAmazonS3"/> from <see cref="S3StorageOptions"/> - shared between
/// <see cref="ServiceCollectionExtensions"/> (production DI wiring) and this project's own
/// integration tests (which construct a client directly against a Testcontainers MinIO, no DI
/// container involved), so the MinIO-specific quirks below are fixed in exactly one place.
/// </summary>
public static class S3ClientFactory
{
    public static IAmazonS3 Create(S3StorageOptions options)
    {
        var config = new AmazonS3Config
        {
            ForcePathStyle = options.ForcePathStyle,
            AuthenticationRegion = options.Region,
        };

        if (!string.IsNullOrEmpty(options.ServiceUrl))
        {
            config.ServiceURL = options.ServiceUrl;
            // The AWS SDK's presigned-URL builder does not reliably infer the scheme from
            // ServiceURL alone - found live running this against a real MinIO container: the
            // presigned PUT/GET URLs came back as https:// even though MinIO serves plain HTTP
            // locally, and a client PUT/GET against them failed the TLS handshake outright. UseHttp
            // is the SDK's own explicit switch for this, driven by whatever scheme the caller's own
            // ServiceUrl specifies (https:// in a real deployment fronted by TLS, http:// for local
            // MinIO) rather than hardcoded either way.
            config.UseHttp = options.ServiceUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            config.RegionEndpoint = RegionEndpoint.GetBySystemName(options.Region);
        }

        return new AmazonS3Client(new BasicAWSCredentials(options.AccessKey, options.SecretKey), config);
    }
}
