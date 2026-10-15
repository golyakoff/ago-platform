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

    // Upstream minio/minio is no longer published on Docker Hub or quay.io; pgsty/minio is the community
    // fork of the same server. Pinned by digest so a re-tag cannot change what CI runs.
    public const string Image = "pgsty/minio:RELEASE.2026-08-04T00-00-00Z@sha256:b6bfe7239bfc83fb90d31612d9704d86039dd714f7904b3f1ad68f211e602372";

    private MinioContainer _container = null!;

    public IAmazonS3 Client { get; private set; } = null!;

    public S3StorageOptions Options { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        _container = new MinioBuilder(Image).WithUsername(Username).WithPassword(Password).Build();
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
