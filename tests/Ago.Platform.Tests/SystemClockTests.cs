using Ago.Platform.Hosting;

namespace Ago.Platform.Tests;

public class SystemClockTests
{
    [Fact]
    public void UtcNow_ReturnsAValueCloseToTheRealClock()
    {
        var clock = new SystemClock();
        var before = DateTimeOffset.UtcNow;

        var reading = clock.UtcNow;

        var after = DateTimeOffset.UtcNow;
        Assert.InRange(reading, before, after);
    }
}
