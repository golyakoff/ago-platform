using Microsoft.EntityFrameworkCore;

namespace Ago.Platform.Persistence.Postgres;

public static class ModelBuilderExtensions
{
    /// <summary>Called from a product's own <c>OnModelCreating</c> to opt its DbContext into the
    /// shared outbox/inbox schema (adr/0017).</summary>
    public static ModelBuilder ApplyOutboxInboxConfiguration(this ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfiguration(new OutboxMessageConfiguration());
        modelBuilder.ApplyConfiguration(new InboxRecordConfiguration());
        return modelBuilder;
    }
}
