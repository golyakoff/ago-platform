using Ago.Platform.Persistence.Postgres;
using Microsoft.EntityFrameworkCore;

namespace Ago.Platform.Integration.Tests;

/// <summary>
/// A throwaway product-shaped DbContext, standing in for a real product's own (AgoChatDbContext today
/// - Ago.Platform.Persistence.Postgres must never reference it directly). Exists only to prove the
/// generic outbox/inbox writer works against *some* DbContext, exactly as adr/0017 requires.
/// </summary>
public sealed class TestDbContext(DbContextOptions<TestDbContext> options) : DbContext(options)
{
    public DbSet<TestWidget> Widgets => Set<TestWidget>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyOutboxInboxConfiguration();

        modelBuilder.Entity<TestWidget>(builder =>
        {
            builder.ToTable("test_widgets");
            builder.HasKey(w => w.Id);
        });
    }
}

/// <summary>Stands in for "the product's own state change" alongside which an outbox/inbox row must
/// be persisted atomically.</summary>
public sealed class TestWidget
{
    public Guid Id { get; init; }

    public string Name { get; init; } = "";
}
