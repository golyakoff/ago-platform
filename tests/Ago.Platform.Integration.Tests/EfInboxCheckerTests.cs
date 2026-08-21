using Ago.Platform.Persistence.Postgres;
using Microsoft.EntityFrameworkCore;

namespace Ago.Platform.Integration.Tests;

/// <summary>docs/architecture/messaging.md: a duplicate delivery is detected, skipped, and acked -
/// proven here by delivering the same (messageId, consumer) pair twice through two independent
/// DbContext instances, exactly as two separate consumer invocations would.</summary>
[Collection(PostgresCollection.Name)]
public sealed class EfInboxCheckerTests(PostgresFixture fixture)
{
    private const string Consumer = "test-consumer";

    [Fact]
    public async Task FirstDelivery_RecordsAndPersistsTheCallersOwnWorkToo()
    {
        var messageId = Guid.NewGuid();
        var widgetId = Guid.NewGuid();

        await using (var db = fixture.CreateDbContext())
        {
            var checker = new EfInboxChecker<TestDbContext>(db, new FakeClock(DateTimeOffset.UtcNow));
            db.Widgets.Add(new TestWidget { Id = widgetId, Name = "first-delivery" });

            var recorded = await checker.TryRecordAndSaveAsync(messageId, Consumer, CancellationToken.None);

            Assert.True(recorded);
        }

        await using var verify = fixture.CreateDbContext();
        Assert.True(await verify.Widgets.AnyAsync(w => w.Id == widgetId, CancellationToken.None));
        Assert.True(await verify.Set<InboxRecord>()
            .AnyAsync(i => i.MessageId == messageId && i.Consumer == Consumer, CancellationToken.None));
    }

    [Fact]
    public async Task DuplicateDelivery_ReturnsFalse_AndDiscardsTheSecondCallsOwnWorkToo()
    {
        var messageId = Guid.NewGuid();
        var firstWidgetId = Guid.NewGuid();
        var secondWidgetId = Guid.NewGuid();

        await using (var first = fixture.CreateDbContext())
        {
            var checker = new EfInboxChecker<TestDbContext>(first, new FakeClock(DateTimeOffset.UtcNow));
            first.Widgets.Add(new TestWidget { Id = firstWidgetId, Name = "first-delivery" });
            var recorded = await checker.TryRecordAndSaveAsync(messageId, Consumer, CancellationToken.None);
            Assert.True(recorded);
        }

        await using (var second = fixture.CreateDbContext())
        {
            var checker = new EfInboxChecker<TestDbContext>(second, new FakeClock(DateTimeOffset.UtcNow));
            // Same messageId/consumer as above - a redelivery. Also stages unrelated work, which
            // must NOT persist either, proving the second delivery's effects are discarded together.
            second.Widgets.Add(new TestWidget { Id = secondWidgetId, Name = "duplicate-delivery" });

            var recorded = await checker.TryRecordAndSaveAsync(messageId, Consumer, CancellationToken.None);

            Assert.False(recorded);
        }

        await using var verify = fixture.CreateDbContext();
        var inboxRowCount = await verify.Set<InboxRecord>()
            .CountAsync(i => i.MessageId == messageId && i.Consumer == Consumer, CancellationToken.None);
        Assert.Equal(1, inboxRowCount);
        Assert.False(await verify.Widgets.AnyAsync(w => w.Id == secondWidgetId, CancellationToken.None));
    }

    [Fact]
    public async Task SameMessage_DifferentConsumers_AreIndependentlyTracked()
    {
        var messageId = Guid.NewGuid();

        await using (var db = fixture.CreateDbContext())
        {
            var checker = new EfInboxChecker<TestDbContext>(db, new FakeClock(DateTimeOffset.UtcNow));
            var recordedForFirst = await checker.TryRecordAndSaveAsync(messageId, "consumer-a", CancellationToken.None);
            var recordedForSecond = await checker.TryRecordAndSaveAsync(messageId, "consumer-b", CancellationToken.None);

            Assert.True(recordedForFirst);
            Assert.True(recordedForSecond);
        }
    }
}
