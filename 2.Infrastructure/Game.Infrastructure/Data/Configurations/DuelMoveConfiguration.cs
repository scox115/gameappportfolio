using Game.Core.Battles;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Game.Infrastructure.Data.Configurations;

public class DuelMoveConfiguration : IEntityTypeConfiguration<DuelMove>
{
    public void Configure(EntityTypeBuilder<DuelMove> builder)
    {
        builder.ToTable("DuelMoves");

        builder.HasKey(m => m.Id);
        builder.Property(m => m.Card).HasConversion<string>().HasMaxLength(20);

        // A replay reads one duel's moves in order.
        builder.HasIndex(m => new { m.BattleId, m.Turn });

        // No foreign key, like the duel's other records: the cleanup worker and account deletion remove
        // a duel's moves along with the duel.
    }
}
