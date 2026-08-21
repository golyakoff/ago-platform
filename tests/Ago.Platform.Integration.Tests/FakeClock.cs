using Ago.Platform.Kernel;

namespace Ago.Platform.Integration.Tests;

public sealed class FakeClock(DateTimeOffset now) : IClock
{
    public DateTimeOffset UtcNow { get; set; } = now;
}
