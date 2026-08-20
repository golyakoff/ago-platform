using Ago.Platform.Kernel;

namespace Ago.Platform.Hosting;

/// <summary>
/// The one place <see cref="DateTimeOffset.UtcNow"/> is read (date-and-time.md). Lives in
/// <c>Hosting</c>, not <c>Kernel</c>: <c>Kernel</c> stays contracts and dependency-free primitives,
/// and <c>Hosting</c> is already where composition-root-adjacent pieces are registered from.
/// </summary>
public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
