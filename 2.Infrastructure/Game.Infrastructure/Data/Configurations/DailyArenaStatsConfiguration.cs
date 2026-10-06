using Game.Core.History;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Game.Infrastructure.Data.Configurations;

public class DailyArenaStatsConfiguration : IEntityTypeConfiguration<DailyArenaStats>
{
    public void Configure(EntityTypeBuilder<DailyArenaStats> builder)
    {
        builder.ToTable("DailyArenaStats");

        builder.HasKey(s => s.Day);

        // Two API instances could count matches for the same day at once.
        builder.Property(s => s.Version).IsConcurrencyToken();
    }
}
