using System.Net;
using Ago.Platform.Abstractions;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Logging;
using Polly;

namespace Ago.Platform.Storage.S3;

/// <summary>
/// `file-storage.md`'s port, implemented against the AWS SDK - MinIO is S3-API-compatible, so no
/// MinIO-specific client exists or is needed; only <see cref="IAmazonS3"/>'s <c>ServiceURL</c>
/// changes between local development and a real deployment (`S3StorageOptions`).
///
/// Every call runs through a shared <see cref="ResiliencePipeline"/> (retry, per-attempt timeout,
/// circuit breaker - `resilience.md`'s S3/MinIO row) and, once that is exhausted, throws
/// <see cref="FileStorageUnavailableException"/> rather than returning a default - unlike
/// <see cref="Abstractions.ICache"/> there is no sensible fallback for "could not presign an upload".
/// The one deliberate exception: <see cref="GetMetadataAsync"/> treats a `404` as the expected
/// "does not exist" outcome, not a failure - retrying or breaking on it would only slow down a normal
/// answer, so the resilience predicates below exclude 4xx responses from both.
/// </summary>
public sealed class S3FileStorage(IAmazonS3 client, S3StorageOptions options, ResiliencePipeline resilience, ILogger<S3FileStorage> logger)
    : IFileStorage
{
    public async Task<PresignedUpload> CreateUploadAsync(ObjectKey key, UploadConstraints constraints, CancellationToken cancellationToken)
    {
        var expiresAt = DateTimeOffset.UtcNow + constraints.Lifetime;
        var request = new GetPreSignedUrlRequest
        {
            BucketName = options.Bucket,
            Key = key.Value,
            Verb = HttpVerb.PUT,
            Expires = expiresAt.UtcDateTime,
            ContentType = constraints.ContentType,
            Protocol = PresignProtocol,
        };

        // 5-13: the size the caller declared is signed into the URL, not merely recorded next to it.
        // SigV4's canonical request covers every header named in X-Amz-SignedHeaders, and the SDK puts
        // whatever is set here into that list - so the store recomputes the signature over the actual
        // PUT's own Content-Length and rejects the request outright when it differs, before accepting a
        // byte. Without this the ceiling existed only in the application that presigned: a client that
        // declared 1 KiB and then PUT 4 GiB straight at the URL was bounded by nothing (proven, and now
        // pinned by S3FileStorageTests' over/under-sized cases against real MinIO). The after-the-fact
        // HEAD check (`file-storage.md` step 4) is unchanged and still runs - it can only refuse to mark
        // an attachment ready, which is a different guarantee from refusing the write.
        request.Headers.ContentLength = constraints.SizeBytes;

        var url = await ExecuteAsync(
            "presign upload",
            key,
            () => client.GetPreSignedURLAsync(request),
            cancellationToken);

        return new PresignedUpload(new Uri(url), expiresAt);
    }

    public async Task<Uri> CreateDownloadUrlAsync(ObjectKey key, TimeSpan lifetime, CancellationToken cancellationToken)
    {
        var url = await ExecuteAsync(
            "presign download",
            key,
            () => client.GetPreSignedURLAsync(new GetPreSignedUrlRequest
            {
                BucketName = options.Bucket,
                Key = key.Value,
                Verb = HttpVerb.GET,
                Expires = (DateTimeOffset.UtcNow + lifetime).UtcDateTime,
                Protocol = PresignProtocol,
            }),
            cancellationToken);

        return new Uri(url);
    }

    // GetPreSignedUrlRequest.Protocol, not AmazonS3Config.UseHttp - found live running this against
    // a real MinIO container: UseHttp governs the client's own outgoing calls (GetObjectMetadataAsync,
    // DeleteObjectAsync), but the presigned-URL builder has its own, separate scheme decision that
    // defaults to HTTPS regardless of UseHttp, and a client PUT/GET against an https:// URL for a
    // plain-HTTP MinIO fails the TLS handshake outright. Driven by the same ServiceUrl scheme
    // S3ClientFactory already reads, so the two never disagree.
    private Protocol PresignProtocol =>
        !string.IsNullOrEmpty(options.ServiceUrl) && options.ServiceUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            ? Protocol.HTTP
            : Protocol.HTTPS;

    public async Task<ObjectMetadata?> GetMetadataAsync(ObjectKey key, CancellationToken cancellationToken)
    {
        try
        {
            return await resilience.ExecuteAsync(
                async ct =>
                {
                    var response = await client.GetObjectMetadataAsync(
                        new GetObjectMetadataRequest { BucketName = options.Bucket, Key = key.Value }, ct);
                    return (ObjectMetadata?)new ObjectMetadata(response.ContentLength, response.Headers.ContentType);
                },
                cancellationToken);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "S3 get-metadata failed against bucket {Bucket}, key {Key}.", options.Bucket, key);
            throw new FileStorageUnavailableException($"Could not read metadata for '{key}'.", ex);
        }
    }

    public async Task DeleteAsync(ObjectKey key, CancellationToken cancellationToken) =>
        await ExecuteAsync(
            "delete",
            key,
            async () =>
            {
                // S3's DELETE is idempotent by design - deleting an object that is already gone is
                // not an error, which is exactly the "tolerate already-gone" behaviour 5-04's sweeper
                // needs and gets for free here.
                await client.DeleteObjectAsync(options.Bucket, key.Value, cancellationToken);
                return true;
            },
            cancellationToken);

    private async Task<T> ExecuteAsync<T>(string operation, ObjectKey key, Func<Task<T>> action, CancellationToken cancellationToken)
    {
        try
        {
            return await resilience.ExecuteAsync(async _ => await action(), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "S3 {Operation} failed against bucket {Bucket}, key {Key}.", operation, options.Bucket, key);
            throw new FileStorageUnavailableException($"Storage operation '{operation}' failed for '{key}'.", ex);
        }
    }
}
