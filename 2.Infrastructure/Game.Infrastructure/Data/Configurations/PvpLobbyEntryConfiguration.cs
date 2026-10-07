using Game.Core.Battles;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Game.Infrastructure.Data.Configurations;

public class PvpLobbyEntryConfiguration : IEntityTypeConfiguration<PvpLobbyEntry>
{
    public void Configure(EntityTypeBuilder<PvpLobbyEntry> builder)
    {
        builder.ToTable("PvpLobby");

        builder.HasKey(e => e.PlayerId);
        builder.Property(e => e.PlayerId).ValueGeneratedNever();
        builder.Property(e => e.Network).HasMaxLength(PvpLobbyEntry.NetworkMaxLength);

        // Pairing looks for the longest-waiting player with the same wager.
        builder.HasIndex(e => new { e.Wager, e.JoinedAt });

        // No foreign key: the lobby is short-lived, and account deletion removes the entry itself.
    }
}
