using Game.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Game.Infrastructure.Data.Configurations;

public class GameMatchConfiguration : IEntityTypeConfiguration<GameMatch>
{
    public void Configure(EntityTypeBuilder<GameMatch> builder)
    {
        builder.ToTable("Matches");

        builder.HasKey(m => m.Id);

        builder.Property(m => m.PlayerOneId)
            .IsRequired();

        builder.Property(m => m.PlayerTwoId)
            .IsRequired();

        builder.Property(m => m.WinnerPlayerId)
            .IsRequired(false);

        builder.Property(m => m.IsCompleted)
            .IsRequired();

        builder.Property(m => m.CreatedAt)
            .IsRequired();
    }
}
