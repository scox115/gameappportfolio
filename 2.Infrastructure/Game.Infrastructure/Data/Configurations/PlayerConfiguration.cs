using Game.Core.Entities;
using Game.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Game.Infrastructure.Data.Configurations;

public class PlayerConfiguration : IEntityTypeConfiguration<Player>
{
    public void Configure(EntityTypeBuilder<Player> builder)
    {
        builder.ToTable("Players");

        builder.HasKey(p => p.Id);

        builder.Property(p => p.Username)
            .IsRequired()
            .HasMaxLength(50);

        // Tell EF Core how to map properties with private setters
        builder.Property(p => p.Gold)
            .IsRequired();

        builder.Property(p => p.Level)
            .IsRequired();

        builder.Property(p => p.ExperiencePoints)
            .IsRequired();
            
        // Purchases and rewards both change gold; this stops two requests spending the same gold.
        builder.Property(p => p.Version)
            .IsConcurrencyToken();

        // New and existing players start at the same PvP rating; the leaderboard sorts on it.
        builder.Property(p => p.Rating)
            .HasDefaultValue(EloRating.StartingRating);
        builder.HasIndex(p => p.Rating);

        // Upgraded cards live in their own table, one row per card the player has upgraded.
        builder.OwnsMany(p => p.CardUpgrades, upgrades =>
        {
            upgrades.ToTable("PlayerCardUpgrades");
            upgrades.WithOwner().HasForeignKey("PlayerId");
            upgrades.Property(u => u.Card)
                .HasConversion<string>()
                .HasMaxLength(20);
            upgrades.HasKey("PlayerId", nameof(CardUpgrade.Card));
        });

        builder.Property(p => p.EquippedTitle)
            .HasConversion<string>()
            .HasMaxLength(30);

        // Titles bought in the Gold Shop, one row per title owned.
        builder.OwnsMany(p => p.Titles, titles =>
        {
            titles.ToTable("PlayerTitles");
            titles.WithOwner().HasForeignKey("PlayerId");
            titles.Property(t => t.Title)
                .HasConversion<string>()
                .HasMaxLength(30);
            titles.HasKey("PlayerId", nameof(OwnedTitle.Title));
        });

        // Optimizes lookups by indexing the username field uniquely
        builder.HasIndex(p => p.Username)
            .IsUnique();
    }
}
