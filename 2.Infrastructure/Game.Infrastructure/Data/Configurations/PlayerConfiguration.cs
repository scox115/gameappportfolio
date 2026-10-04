using Game.Core.Entities;
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
            
        // Optimizes lookups by indexing the username field uniquely
        builder.HasIndex(p => p.Username)
            .IsUnique();
    }
}
