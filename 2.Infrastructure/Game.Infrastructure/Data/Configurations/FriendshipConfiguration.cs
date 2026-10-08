using Game.Core.Social;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Game.Infrastructure.Data.Configurations;

public class FriendshipConfiguration : IEntityTypeConfiguration<Friendship>
{
    public void Configure(EntityTypeBuilder<Friendship> builder)
    {
        builder.ToTable("Friendships");

        builder.HasKey(f => f.Id);
        builder.Property(f => f.Id).ValueGeneratedNever();
        builder.Property(f => f.PairKey).HasMaxLength(Friendship.PairKeyLength).IsRequired();
        builder.Ignore(f => f.IsAccepted);

        // One row per pair, whoever asked; and a hero's list, from either side.
        builder.HasIndex(f => f.PairKey).IsUnique();
        builder.HasIndex(f => f.RequesterId);
        builder.HasIndex(f => f.AddresseeId);

        // No foreign keys, like the lobby and reports: account deletion removes a hero's friendships itself.
    }
}
