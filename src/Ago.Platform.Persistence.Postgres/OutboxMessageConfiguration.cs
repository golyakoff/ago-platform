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
        // `7-01`: nullable, unbounded W3C traceparent length is fixed (00-{32 hex}-{16 hex}-{2 hex},
        // 55 chars) but this stores it as text rather than a hardcoded varchar(55) - a wider future
        // trace-context format (W3C tracestate, or a different propagator) should not need a schema
        // change to fit.
        builder.Property(o => o.TraceContext).HasColumnName("trace_context");
        builder.Property(o => o.PublishedAt).HasColumnName("published_at");
        builder.Property(o => o.Attempts).HasColumnName("attempts");

        // data-model.md: "the dispatcher must never scan already-published rows."
        builder.HasIndex(o => o.Id)
            .HasDatabaseName("ix_outbox_unpublished")
            .HasFilter("published_at IS NULL");
    }
}
