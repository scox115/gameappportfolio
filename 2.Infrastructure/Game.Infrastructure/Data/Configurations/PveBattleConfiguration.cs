using Game.Core.Battles;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Game.Infrastructure.Data.Configurations;

public class PveBattleConfiguration : IEntityTypeConfiguration<PveBattle>
{
    public void Configure(EntityTypeBuilder<PveBattle> builder)
    {
        builder.ToTable("PveBattles");

        builder.HasKey(b => b.Id);

        builder.Property(b => b.Status)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(b => b.BossNextMove)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(b => b.LastCardPlayed)
            .HasConversion<string>()
            .HasMaxLength(20);

        // Two turns submitted at once must not both apply to the same battle state.
        builder.Property(b => b.Version)
            .IsConcurrencyToken();

        // Finds a player's battle in progress when they enter the arena.
        builder.HasIndex(b => new { b.PlayerId, b.Status });
    }
}
