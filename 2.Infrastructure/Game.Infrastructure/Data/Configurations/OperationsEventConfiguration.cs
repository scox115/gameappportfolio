using Game.Core.Operations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Game.Infrastructure.Data.Configurations;

public class OperationsEventConfiguration : IEntityTypeConfiguration<OperationsEvent>
{
    public void Configure(EntityTypeBuilder<OperationsEvent> builder)
    {
        builder.ToTable("OperationsEvents");

        builder.HasKey(e => e.Id);

        // Written by name from the restore drill's SQL, so stored as text rather than a number.
        builder.Property(e => e.Kind).HasConversion<string>().HasMaxLength(20);
        builder.Property(e => e.Version).HasMaxLength(OperationsEvent.VersionMaxLength);
        builder.Property(e => e.Revision).HasMaxLength(OperationsEvent.RevisionMaxLength);
        builder.Property(e => e.Detail).HasMaxLength(OperationsEvent.DetailMaxLength);

        // The status page reads the newest events of each kind.
        builder.HasIndex(e => new { e.Kind, e.OccurredAt });

        // Every API replica that starts serving players tries to record its release; only the first succeeds.
        builder.HasIndex(e => e.Revision).IsUnique().HasFilter("[Revision] IS NOT NULL");
    }
}
