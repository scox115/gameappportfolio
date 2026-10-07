using Game.Core.Admin;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Game.Infrastructure.Data.Configurations;

public class AuditLogEntryConfiguration : IEntityTypeConfiguration<AuditLogEntry>
{
    public void Configure(EntityTypeBuilder<AuditLogEntry> builder)
    {
        builder.ToTable("AuditLog");

        builder.HasKey(e => e.Id);

        // Stored as text so the table reads plainly in a query editor.
        builder.Property(e => e.Action).HasConversion<string>().HasMaxLength(20);
        builder.Property(e => e.ActorName).HasMaxLength(AuditLogEntry.NameMaxLength).IsRequired();
        builder.Property(e => e.TargetName).HasMaxLength(AuditLogEntry.NameMaxLength).IsRequired();
        builder.Property(e => e.Reason).HasMaxLength(AuditLogEntry.ReasonMaxLength).IsRequired();
        builder.Property(e => e.Detail).HasMaxLength(AuditLogEntry.DetailMaxLength);

        // No foreign key to the player: the record outlives a deleted account.
        builder.HasIndex(e => new { e.TargetId, e.At });
        builder.HasIndex(e => e.At);
    }
}
