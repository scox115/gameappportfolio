using Game.Core.Seasons;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Game.Infrastructure.Data.Configurations;

public class ClosedSeasonConfiguration : IEntityTypeConfiguration<ClosedSeason>
{
    public void Configure(EntityTypeBuilder<ClosedSeason> builder)
    {
        builder.ToTable("ClosedSeasons");
        // One row per season: a second replica closing the same season fails on the key.
        builder.HasKey(s => s.SeasonStart);
        builder.Ignore(s => s.Season);
    }
}
