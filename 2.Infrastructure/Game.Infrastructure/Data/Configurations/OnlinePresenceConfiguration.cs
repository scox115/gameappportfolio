using Game.Core.Battles;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Game.Infrastructure.Data.Configurations;

public class OnlinePresenceConfiguration : IEntityTypeConfiguration<OnlinePresence>
{
    public void Configure(EntityTypeBuilder<OnlinePresence> builder)
    {
        builder.ToTable("OnlinePresence");

        builder.HasKey(p => p.ConnectionId);
        builder.Property(p => p.ConnectionId).HasMaxLength(OnlinePresence.ConnectionIdMaxLength);

        // Counting the players seen recently, and clearing out the rest.
        builder.HasIndex(p => new { p.SeenAt, p.PlayerId });

        // No foreign key: rows last as long as a browser tab, and account deletion removes them itself.
    }
}
