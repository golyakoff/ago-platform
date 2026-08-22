using Ago.Platform.Storage.S3;
using Amazon.S3;
using Amazon.S3.Model;
using Testcontainers.Minio;

namespace Ago.Platform.Integration.Tests;

public sealed class MinioFixture : IAsyncLifetime
{
    private const string Username = "ago-test";
    private const string Password = "ago-test-local-dev";
    public const string Bucket = "attachments";

    private MinioContainer _container = null!;

    public IAmazonS3 Client { get; private set; } = null!;

    public S3StorageOptions Options { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        _container = new MinioBuilder("minio/minio:RELEASE.2025-09-07T16-13-09Z").WithUsername(Username).WithPassword(Password).Build();
        await _container.StartAsync();

        Options = new S3StorageOptions
        {
            ServiceUrl = _container.GetConnectionString(),
            AccessKey = _container.GetAccessKey(),
            SecretKey = _container.GetSecretKey(),
            Bucket = Bucket,
            ForcePathStyle = true,
        };

        Client = S3ClientFactory.Create(Options);
        await Client.PutBucketAsync(new PutBucketRequest { BucketName = Bucket });
    }

    public async Task DisposeAsync()
    {
        Client.Dispose();
        await _container.DisposeAsync();
    }
}

[CollectionDefinition(Name)]
public sealed class MinioCollection : ICollectionFixture<MinioFixture>
{
    public const string Name = "Minio";
}
