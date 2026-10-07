using Game.Core.Moderation;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Game.Infrastructure.Data.Configurations;

public class PlayerReportConfiguration : IEntityTypeConfiguration<PlayerReport>
{
    public void Configure(EntityTypeBuilder<PlayerReport> builder)
    {
        builder.ToTable("PlayerReports");

        builder.HasKey(r => r.Id);

        builder.Property(r => r.Reason).HasConversion<string>().HasMaxLength(20);
        builder.Property(r => r.Outcome).HasConversion<string>().HasMaxLength(20);
        builder.Property(r => r.Note).HasMaxLength(PlayerReport.NoteMaxLength);
        builder.Ignore(r => r.IsOpen);

        // The admin queue: open reports about a hero, and whether a player already reported it.
        builder.HasIndex(r => new { r.TargetId, r.Reason, r.ResolvedAt });
        builder.HasIndex(r => new { r.ReporterId, r.TargetId, r.Reason });
        builder.HasIndex(r => r.ResolvedAt); // for the cleanup worker

        // No foreign keys, like the audit log: account deletion removes a player's reports itself.
    }
}
