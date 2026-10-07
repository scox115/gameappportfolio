using Game.Core.Bounties;
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
            
        builder.Property(p => p.Class)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        // Purchases and rewards both change gold; this stops two requests spending the same gold.
        builder.Property(p => p.Version)
            .IsConcurrencyToken();

        // New and existing players start at the same PvP rating; the leaderboard sorts on it.
        builder.Property(p => p.Rating)
            .HasDefaultValue(EloRating.StartingRating);
        builder.HasIndex(p => p.Rating);

        // For the cleanup worker, which deletes guests nobody kept (see docs/adr/0030-guest-play.md).
        builder.HasIndex(p => p.GuestSince);

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

        builder.Property(p => p.EquippedFrame)
            .HasConversion<string>()
            .HasMaxLength(30);

        builder.Property(p => p.EquippedCardSkin)
            .HasConversion<string>()
            .HasMaxLength(30);

        // Prestige cosmetics bought in the Gold Shop, one row per item owned.
        builder.OwnsMany(p => p.Cosmetics, cosmetics =>
        {
            cosmetics.ToTable("PlayerCosmetics");
            cosmetics.WithOwner().HasForeignKey("PlayerId");
            cosmetics.Property(c => c.Cosmetic)
                .HasConversion<string>()
                .HasMaxLength(30);
            cosmetics.HasKey("PlayerId", nameof(OwnedCosmetic.Cosmetic));
        });

        // Progress on today's daily bounties. The day is part of the key so yesterday's rows can be
        // replaced by today's in the same save.
        builder.OwnsMany(p => p.Bounties, bounties =>
        {
            bounties.ToTable("PlayerBounties");
            bounties.WithOwner().HasForeignKey("PlayerId");
            bounties.Property(b => b.Bounty)
                .HasConversion<string>()
                .HasMaxLength(30);
            bounties.HasKey("PlayerId", nameof(BountyProgress.Day), nameof(BountyProgress.Bounty));
        });

        // PvP wins and losses for each class the hero has dueled as, one row per class.
        builder.OwnsMany(p => p.ClassRecords, records =>
        {
            records.ToTable("PlayerClassRecords");
            records.WithOwner().HasForeignKey("PlayerId");
            records.Property(r => r.Class)
                .HasConversion<string>()
                .HasMaxLength(20);
            records.HasKey("PlayerId", nameof(ClassRecord.Class));
        });

        // Optimizes lookups by indexing the username field uniquely
        builder.HasIndex(p => p.Username)
            .IsUnique();
    }
}
