using Game.Infrastructure.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Game.Infrastructure.Data.Configurations;

public class HubMessageConfiguration : IEntityTypeConfiguration<HubMessage>
{
    public void Configure(EntityTypeBuilder<HubMessage> builder)
    {
        builder.ToTable("HubMessages");

        builder.HasKey(m => m.Id);
        builder.Property(m => m.Hub).HasMaxLength(HubMessage.HubMaxLength).IsRequired();
        builder.Property(m => m.Method).HasMaxLength(HubMessage.MethodMaxLength).IsRequired();
        builder.Property(m => m.Arguments).IsRequired();
        builder.Ignore(m => m.Recipients);

        // Replicas read the last few seconds of messages, and delete old ones.
        builder.HasIndex(m => m.CreatedAt);
    }
}
