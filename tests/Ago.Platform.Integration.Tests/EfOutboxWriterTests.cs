using Ago.Platform.Abstractions;
using Ago.Platform.Persistence.Postgres;
using Microsoft.EntityFrameworkCore;

namespace Ago.Platform.Integration.Tests;

/// <summary>adr/0005, adr/0017: an outbox row and the state change it describes are persisted by
/// exactly one SaveChangesAsync call, or neither is. Real Postgres throughout - testing.md: never
/// mock the database for a guarantee the schema itself provides.</summary>
[Collection(PostgresCollection.Name)]
public sealed class EfOutboxWriterTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Enqueue_PersistsAlongsideUnrelatedChange_OnCallersOwnSaveChanges()
    {
        await using var db = fixture.CreateDbContext();
        var writer = new EfOutboxWriter<TestDbContext>(db);

        var widgetId = Guid.NewGuid();
        db.Widgets.Add(new TestWidget { Id = widgetId, Name = "widget-1" });
        var envelope = MakeEnvelope();
        writer.Enqueue(envelope);

        await db.SaveChangesAsync(CancellationToken.None);

        await using var verify = fixture.CreateDbContext();
        Assert.True(await verify.Widgets.AnyAsync(w => w.Id == widgetId, CancellationToken.None));
        var persisted = await verify.Set<OutboxMessage>().SingleAsync(o => o.Id == envelope.MessageId, CancellationToken.None);
        Assert.Equal(envelope.Type, persisted.Type);
        Assert.Equal(envelope.PartitionKey, persisted.PartitionKey);
        Assert.Null(persisted.PublishedAt);
    }

    [Fact]
    public async Task Enqueue_WhenCallerNeverSaves_NothingIsPersisted()
    {
        await using var db = fixture.CreateDbContext();
        var writer = new EfOutboxWriter<TestDbContext>(db);
        var envelope = MakeEnvelope();

        writer.Enqueue(envelope);
        // No SaveChangesAsync call.

        await using var verify = fixture.CreateDbContext();
        Assert.False(await verify.Set<OutboxMessage>().AnyAsync(o => o.Id == envelope.MessageId, CancellationToken.None));
    }

    [Fact]
    public async Task Enqueue_WhenTheUnrelatedChangeFailsToSave_TheOutboxRowIsRolledBackToo()
    {
        var sharedId = Guid.NewGuid();

        await using (var first = fixture.CreateDbContext())
        {
            first.Widgets.Add(new TestWidget { Id = sharedId, Name = "first" });
            await first.SaveChangesAsync(CancellationToken.None);
        }

        await using var conflicting = fixture.CreateDbContext();
        var writer = new EfOutboxWriter<TestDbContext>(conflicting);
        // Same primary key as the row already committed above - SaveChangesAsync must fail.
        conflicting.Widgets.Add(new TestWidget { Id = sharedId, Name = "second" });
        var envelope = MakeEnvelope();
        writer.Enqueue(envelope);

        await Assert.ThrowsAsync<DbUpdateException>(
            () => conflicting.SaveChangesAsync(CancellationToken.None));

        await using var verify = fixture.CreateDbContext();
        Assert.False(await verify.Set<OutboxMessage>().AnyAsync(o => o.Id == envelope.MessageId, CancellationToken.None));
    }

    private static EventEnvelope MakeEnvelope() => new(
        MessageId: Guid.NewGuid(),
        Type: "TestEvent",
        Version: 1,
        PartitionKey: Guid.NewGuid().ToString(),
        OccurredAt: DateTimeOffset.UtcNow,
        CorrelationId: Guid.NewGuid(),
        Payload: "{}");
}
