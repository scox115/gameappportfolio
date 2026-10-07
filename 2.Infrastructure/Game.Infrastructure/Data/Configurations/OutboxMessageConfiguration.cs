using Game.Infrastructure.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Game.Infrastructure.Data.Configurations;

public class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        builder.ToTable("OutboxMessages");

        // The id is the match id, so the same match can never be queued twice.
        builder.HasKey(m => m.Id);
        builder.Property(m => m.Id).ValueGeneratedNever();

        builder.Property(m => m.Queue).HasMaxLength(OutboxMessage.QueueMaxLength).IsRequired();
        builder.Property(m => m.Payload).IsRequired();
        builder.Property(m => m.LastError).HasMaxLength(OutboxMessage.ErrorMaxLength);

        builder.Property(m => m.ClaimToken).IsConcurrencyToken();

        builder.HasIndex(m => m.CreatedAt); // the relay sends oldest first
    }
}
