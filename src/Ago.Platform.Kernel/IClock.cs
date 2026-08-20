namespace Ago.Platform.Kernel;

/// <summary>
/// The only source of the current instant. Domain and Application take time as a parameter instead
/// of reading a clock directly (docs/conventions/date-and-time.md); this is what they take it from.
/// </summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}
