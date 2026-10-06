using Game.Core.Battles;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Game.Infrastructure.Data.Configurations;

public class PvpBattleConfiguration : IEntityTypeConfiguration<PvpBattle>
{
    public void Configure(EntityTypeBuilder<PvpBattle> builder)
    {
        builder.ToTable("PvpBattles");

        builder.HasKey(b => b.Id);

        builder.Property(b => b.Status)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(b => b.PlayerOneClass)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(b => b.PlayerTwoClass)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(b => b.EndReason)
            .HasConversion<string>()
            .HasMaxLength(20);

        builder.Property(b => b.PlayerOneLastCard)
            .HasConversion<string>()
            .HasMaxLength(20);

        builder.Property(b => b.PlayerTwoLastCard)
            .HasConversion<string>()
            .HasMaxLength(20);

        // A move, a timeout and a forfeit can race; only one may apply to a given battle state.
        builder.Property(b => b.Version)
            .IsConcurrencyToken();

        // Finds a player's battle in progress, and battles whose turn timer has run out.
        builder.HasIndex(b => new { b.PlayerOneId, b.Status });
        builder.HasIndex(b => new { b.PlayerTwoId, b.Status });
        builder.HasIndex(b => new { b.Status, b.TurnDeadline });

        // Duels started before loadouts existed are plain level-1, 100 HP fights.
        builder.Property(b => b.PlayerOneMaxHp).HasDefaultValue(PvpBattle.BasePlayerMaxHp);
        builder.Property(b => b.PlayerTwoMaxHp).HasDefaultValue(PvpBattle.BasePlayerMaxHp);
        builder.Property(b => b.PlayerOneFireballLevel).HasDefaultValue(1);
        builder.Property(b => b.PlayerOneHolyShieldLevel).HasDefaultValue(1);
        builder.Property(b => b.PlayerOneDragonClawLevel).HasDefaultValue(1);
        builder.Property(b => b.PlayerTwoFireballLevel).HasDefaultValue(1);
        builder.Property(b => b.PlayerTwoHolyShieldLevel).HasDefaultValue(1);
        builder.Property(b => b.PlayerTwoDragonClawLevel).HasDefaultValue(1);
    }
}
