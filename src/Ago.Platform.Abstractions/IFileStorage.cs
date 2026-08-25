namespace Ago.Platform.Abstractions;

/// <summary>
/// `file-storage.md`'s port: bytes never pass through the process holding this port - every method
/// here either issues a short-lived presigned URL a client uses directly against storage, or answers
/// a metadata question, never streams a byte itself. Generic technical infrastructure, the same
/// placement as <see cref="ICache"/>/<see cref="IEventPublisher"/> (`clean-architecture.md`'s
/// qualifying rule) - a product's own key-naming scheme (site/conversation/attachment ids) is not
/// this port's concern, only <see cref="ObjectKey"/>'s opaque string is.
///
/// Failure mode: unlike <see cref="ICache"/> (which always degrades to a miss - a stale/absent cache
/// entry is never wrong, only slower) there is no sensible fallback for "could not presign an upload"
/// or "could not check whether an object exists" - `resilience.md`'s own S3/MinIO row calls for
/// timeout, retry and a circuit breaker on the failure-prone calls, and when those are exhausted this
/// port throws <see cref="FileStorageUnavailableException"/> rather than returning a default. The one
/// exception is <see cref="GetMetadataAsync"/> returning <see langword="null"/> for "genuinely does
/// not exist" - that is an expected outcome, not a failure.
/// </summary>
public interface IFileStorage
{
    Task<PresignedUpload> CreateUploadAsync(ObjectKey key, UploadConstraints constraints, CancellationToken cancellationToken);

    Task<Uri> CreateDownloadUrlAsync(ObjectKey key, TimeSpan lifetime, CancellationToken cancellationToken);

    Task<ObjectMetadata?> GetMetadataAsync(ObjectKey key, CancellationToken cancellationToken);

    Task DeleteAsync(ObjectKey key, CancellationToken cancellationToken);
}

/// <summary>
/// An object's identity within storage - a plain string wrapper, the same shape as
/// <see cref="CacheKey"/> and for the same reason: the product decides the namespacing
/// (`site/{site_id}/conv/{conversation_id}/{uuid7}{ext}`, `file-storage.md`), this port only ever
/// sees the finished path.
/// </summary>
public readonly record struct ObjectKey(string Value)
{
    public override string ToString() => Value;
}

/// <summary>
/// What the caller is asking storage to accept. A presigned PUT is scoped to exactly this content
/// type, exactly <paramref name="SizeBytes"/> bytes, and expires after <paramref name="Lifetime"/> -
/// all three are signed into the URL, so the store itself refuses a request that does not match
/// rather than accepting bytes an application would later have to disown.
///
/// **Exact, not a ceiling** (renamed from `MaxSizeBytes` in `5-13`, when the value turned out to be
/// captured and never used): a presigned PUT has no way to express "at most N" - a range condition
/// (`content-length-range`) exists only in a presigned *POST* policy document, which would mean every
/// client switching from a raw PUT to a multipart form POST for the same outcome. Signing the exact
/// length is the smaller change and the stronger property, and it costs nothing: a caller that wants
/// a ceiling checks its own ceiling before calling (it has to anyway - the store cannot know a
/// product's quota), and then declares the size it actually intends to upload.
///
/// The after-upload verification (`GetMetadataAsync`, `file-storage.md`'s "a client's claim is never
/// trusted") is unchanged and still worth running - it is what catches a *content type* the store
/// recorded differently, and it stays the layer that decides whether an object counts as usable.
/// </summary>
public sealed record UploadConstraints(string ContentType, long SizeBytes, TimeSpan Lifetime);

public sealed record PresignedUpload(Uri Url, DateTimeOffset ExpiresAt);

public sealed record ObjectMetadata(long SizeBytes, string ContentType);

/// <summary>Thrown when a retried, circuit-broken call to storage still failed - the AWS SDK's own
/// exception type never appears above the `Ago.Platform.Storage.S3` boundary
/// (`clean-architecture.md`).</summary>
public sealed class FileStorageUnavailableException(string message, Exception? innerException = null)
    : Exception(message, innerException);
