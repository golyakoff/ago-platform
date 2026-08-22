using Ago.Platform.Abstractions;
using Ago.Platform.Storage.S3;
using Microsoft.Extensions.Logging.Abstractions;
using Polly;

namespace Ago.Platform.Integration.Tests;

/// <summary>
/// `5-02`'s Done-when: a real MinIO container (Testcontainers), no mocking (testing.md). Every test
/// uses a presigned URL exactly the way a browser would - a bare <see cref="HttpClient"/> PUT/GET,
/// never <see cref="IFileStorage"/> itself for the byte transfer, since the whole point of
/// `adr/0008` is that this port never touches bytes.
/// </summary>
[Collection(MinioCollection.Name)]
public sealed class S3FileStorageTests(MinioFixture fixture)
{
    private static readonly HttpClient Http = new();

    [Fact]
    public async Task CreateUploadAsync_ThenPuttingDirectlyToTheUrl_MakesTheObjectReadableViaGetMetadata()
    {
        var storage = CreateStorage();
        var key = new ObjectKey($"test/{Guid.NewGuid():N}.txt");
        var body = "hello from 5-02"u8.ToArray();

        var presigned = await storage.CreateUploadAsync(
            key, new UploadConstraints("text/plain", body.Length, TimeSpan.FromMinutes(5)), CancellationToken.None);

        using var putContent = new ByteArrayContent(body);
        putContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain");
        var putResponse = await Http.PutAsync(presigned.Url, putContent);
        putResponse.EnsureSuccessStatusCode();

        var metadata = await storage.GetMetadataAsync(key, CancellationToken.None);

        Assert.NotNull(metadata);
        Assert.Equal(body.Length, metadata.SizeBytes);
        Assert.Equal("text/plain", metadata.ContentType);
    }

    [Fact]
    public async Task CreateDownloadUrlAsync_UsedDirectly_ReturnsTheUploadedBytes()
    {
        var storage = CreateStorage();
        var key = new ObjectKey($"test/{Guid.NewGuid():N}.txt");
        var body = "download me"u8.ToArray();
        await UploadDirectlyAsync(storage, key, body, "text/plain");

        var downloadUrl = await storage.CreateDownloadUrlAsync(key, TimeSpan.FromMinutes(5), CancellationToken.None);
        var downloaded = await Http.GetByteArrayAsync(downloadUrl);

        Assert.Equal(body, downloaded);
    }

    [Fact]
    public async Task GetMetadataAsync_ForAnObjectThatWasNeverUploaded_ReturnsNull()
    {
        var storage = CreateStorage();
        var key = new ObjectKey($"test/{Guid.NewGuid():N}.txt");

        var metadata = await storage.GetMetadataAsync(key, CancellationToken.None);

        Assert.Null(metadata);
    }

    [Fact]
    public async Task DeleteAsync_ThenGetMetadataAsync_ReturnsNull()
    {
        var storage = CreateStorage();
        var key = new ObjectKey($"test/{Guid.NewGuid():N}.txt");
        await UploadDirectlyAsync(storage, key, "gone soon"u8.ToArray(), "text/plain");

        await storage.DeleteAsync(key, CancellationToken.None);
        var metadata = await storage.GetMetadataAsync(key, CancellationToken.None);

        Assert.Null(metadata);
    }

    [Fact]
    public async Task DeleteAsync_ForAnObjectThatWasNeverUploaded_DoesNotThrow()
    {
        var storage = CreateStorage();
        var key = new ObjectKey($"test/{Guid.NewGuid():N}.txt");

        await storage.DeleteAsync(key, CancellationToken.None); // S3 DELETE is idempotent - no exception expected.
    }

    [Fact]
    public async Task CreateUploadAsync_TheDeclaredContentTypeIsPinnedIntoTheSignature_AMismatchedPutIsRejected()
    {
        var storage = CreateStorage();
        var key = new ObjectKey($"test/{Guid.NewGuid():N}.txt");

        var presigned = await storage.CreateUploadAsync(
            key, new UploadConstraints("text/plain", 5, TimeSpan.FromMinutes(5)), CancellationToken.None);

        using var wrongTypeContent = new ByteArrayContent("hello"u8.ToArray());
        wrongTypeContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        var response = await Http.PutAsync(presigned.Url, wrongTypeContent);

        Assert.False(response.IsSuccessStatusCode);
    }

    private static async Task UploadDirectlyAsync(IFileStorage storage, ObjectKey key, byte[] body, string contentType)
    {
        var presigned = await storage.CreateUploadAsync(
            key, new UploadConstraints(contentType, body.Length, TimeSpan.FromMinutes(5)), CancellationToken.None);
        using var content = new ByteArrayContent(body);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        var response = await Http.PutAsync(presigned.Url, content);
        response.EnsureSuccessStatusCode();
    }

    private S3FileStorage CreateStorage() => new(
        fixture.Client, fixture.Options, TestResiliencePipeline, NullLogger<S3FileStorage>.Instance);

    private static readonly ResiliencePipeline TestResiliencePipeline = new ResiliencePipelineBuilder()
        .AddTimeout(TimeSpan.FromSeconds(5))
        .Build();
}
