using Game.Core.History;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Game.Infrastructure.Data.Configurations;

public class MatchHistoryEntryConfiguration : IEntityTypeConfiguration<MatchHistoryEntry>
{
    public void Configure(EntityTypeBuilder<MatchHistoryEntry> builder)
    {
        builder.ToTable("MatchHistory");

        builder.HasKey(e => e.Id);

        builder.Property(e => e.Kind).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(e => e.Class).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(e => e.OpponentClass).HasConversion<string>().HasMaxLength(20);
        builder.Property(e => e.Difficulty).HasConversion<string>().HasMaxLength(20);
        builder.Property(e => e.EndReason).HasConversion<string>().HasMaxLength(20);
        builder.Property(e => e.OpponentName).HasMaxLength(64).IsRequired();

        // RabbitMQ can deliver a message more than once; one entry per hero per match makes a
        // second delivery fail instead of doubling the history.
        builder.HasIndex(e => new { e.MatchId, e.PlayerId }).IsUnique();

        // A hero's latest matches.
        builder.HasIndex(e => new { e.PlayerId, e.PlayedAt });
    }
}
