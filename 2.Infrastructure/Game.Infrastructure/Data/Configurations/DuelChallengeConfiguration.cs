using Game.Core.Social;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Game.Infrastructure.Data.Configurations;

public class DuelChallengeConfiguration : IEntityTypeConfiguration<DuelChallenge>
{
    public void Configure(EntityTypeBuilder<DuelChallenge> builder)
    {
        builder.ToTable("DuelChallenges");

        // One open challenge per challenger: sending another replaces it.
        builder.HasKey(c => c.ChallengerId);
        builder.Property(c => c.ChallengerId).ValueGeneratedNever();
        builder.Property(c => c.ChallengerNetwork).HasMaxLength(DuelChallenge.NetworkMaxLength);
        builder.HasIndex(c => c.Id).IsUnique();
        builder.HasIndex(c => c.ChallengedId);

        // No foreign keys: challenges last a minute, and account deletion removes them itself.
    }
}
