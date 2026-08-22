using System.ComponentModel.DataAnnotations;

namespace Ago.Platform.Storage.S3;

/// <summary>
/// Bound from <c>Storage:S3:*</c> (`naming-and-structure.md`'s naming pattern, `RabbitMqOptions`'s
/// own precedent) and validated at startup - a typo in a key must fail the pod, not silently disable
/// attachments.
/// </summary>
public sealed class S3StorageOptions
{
    public const string SectionName = "Storage:S3";

    /// <summary>MinIO's own S3-compatible endpoint (e.g. <c>http://minio:9000</c> in-cluster);
    /// unset (<see langword="null"/>) for a real AWS S3 deployment, where the SDK resolves the
    /// endpoint from <see cref="Region"/> instead.</summary>
    public string? ServiceUrl { get; set; }

    [Required]
    public string AccessKey { get; set; } = "";

    [Required]
    public string SecretKey { get; set; } = "";

    [Required]
    public string Bucket { get; set; } = "";

    public string Region { get; set; } = "us-east-1";

    /// <summary>MinIO (and most S3-compatible stores) need path-style addressing
    /// (<c>http://host/bucket/key</c>); real AWS S3 defaults to virtual-hosted-style
    /// (<c>http://bucket.host/key</c>) and does not require this.</summary>
    public bool ForcePathStyle { get; set; } = true;

    public TimeSpan DefaultUploadLifetime { get; set; } = TimeSpan.FromMinutes(10);

    public TimeSpan DefaultDownloadLifetime { get; set; } = TimeSpan.FromMinutes(15);
}
