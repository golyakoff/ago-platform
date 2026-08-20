namespace Ago.Platform.Kernel;

/// <summary>
/// Default <see cref="IIdGenerator"/>: RFC 9562 UUID version 7, time-ordered so primary-key inserts
/// stay B-tree-friendly instead of fragmenting the way random UUIDs do (docs/architecture/data-model.md).
/// </summary>
public sealed class UuidV7Generator : IIdGenerator
{
    public Guid NewId(DateTimeOffset now) => Guid.CreateVersion7(now);
}
