using Ago.Platform.Kernel;

namespace Ago.Platform.Tests;

public class UuidV7GeneratorTests
{
    private readonly UuidV7Generator _generator = new();

    [Fact]
    public void NewId_LaterInstant_SortsAfterEarlierInstant()
    {
        var earlier = new DateTimeOffset(2026, 8, 20, 10, 0, 0, TimeSpan.Zero);
        var later = earlier.AddSeconds(1);

        var earlierId = _generator.NewId(earlier);
        var laterId = _generator.NewId(later);

        Assert.True(CompareAsBytes(earlierId, laterId) < 0);
    }

    [Fact]
    public void NewId_SameInstantRepeated_StillProducesDistinctIds()
    {
        var now = new DateTimeOffset(2026, 8, 20, 10, 0, 0, TimeSpan.Zero);

        var first = _generator.NewId(now);
        var second = _generator.NewId(now);

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void NewId_ManyInstantsAscending_ProducesAGapFreeAscendingRun()
    {
        var start = new DateTimeOffset(2026, 8, 20, 10, 0, 0, TimeSpan.Zero);
        var ids = Enumerable.Range(0, 50)
            .Select(i => _generator.NewId(start.AddMilliseconds(i * 10)))
            .ToList();

        var sorted = ids.OrderBy(id => id, Comparer<Guid>.Create(CompareAsBytes)).ToList();

        Assert.Equal(sorted, ids);
    }

    // UUID v7's time-ordering is defined over its big-endian byte layout, not over Guid's own
    // CompareTo (which does not compare byte-for-byte) - see RFC 9562 and Guid.CreateVersion7.
    private static int CompareAsBytes(Guid left, Guid right)
    {
        Span<byte> leftBytes = stackalloc byte[16];
        Span<byte> rightBytes = stackalloc byte[16];
        left.TryWriteBytes(leftBytes, bigEndian: true, out _);
        right.TryWriteBytes(rightBytes, bigEndian: true, out _);
        return leftBytes.SequenceCompareTo(rightBytes);
    }
}
