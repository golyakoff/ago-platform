namespace Ago.Platform.Kernel;

/// <summary>
/// Produces identifiers that sort in generation order. Takes the instant as a parameter, same as
/// <see cref="IClock"/> callers do, so a generated ordering is reproducible in a test without a
/// fake clock.
/// </summary>
public interface IIdGenerator
{
    Guid NewId(DateTimeOffset now);
}
