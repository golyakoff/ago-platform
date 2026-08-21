using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ago.Platform.Persistence.Postgres;

public sealed class InboxRecordConfiguration : IEntityTypeConfiguration<InboxRecord>
{
    public void Configure(EntityTypeBuilder<InboxRecord> builder)
    {
        builder.ToTable("inbox");
        builder.HasKey(i => new { i.MessageId, i.Consumer });

        builder.Property(i => i.MessageId).HasColumnName("message_id");
        builder.Property(i => i.Consumer).HasColumnName("consumer");
        builder.Property(i => i.ProcessedAt).HasColumnName("processed_at");
    }
}
