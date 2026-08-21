using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ago.Platform.Persistence.Postgres;

public sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        builder.ToTable("outbox");
        builder.HasKey(o => o.Id);

        builder.Property(o => o.Id).HasColumnName("id");
        builder.Property(o => o.OccurredAt).HasColumnName("occurred_at");
        builder.Property(o => o.Type).HasColumnName("type");
        builder.Property(o => o.Version).HasColumnName("version");
        builder.Property(o => o.Payload).HasColumnName("payload").HasColumnType("jsonb");
        builder.Property(o => o.PartitionKey).HasColumnName("partition_key");
        builder.Property(o => o.CorrelationId).HasColumnName("correlation_id");
        builder.Property(o => o.PublishedAt).HasColumnName("published_at");
        builder.Property(o => o.Attempts).HasColumnName("attempts");

        // data-model.md: "the dispatcher must never scan already-published rows."
        builder.HasIndex(o => o.Id)
            .HasDatabaseName("ix_outbox_unpublished")
            .HasFilter("published_at IS NULL");
    }
}
