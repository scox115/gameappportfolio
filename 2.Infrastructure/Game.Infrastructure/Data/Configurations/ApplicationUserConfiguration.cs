using Game.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Game.Infrastructure.Data.Configurations;

// Identity maps the account itself; this adds the game's own columns.
public class ApplicationUserConfiguration : IEntityTypeConfiguration<ApplicationUser>
{
    public void Configure(EntityTypeBuilder<ApplicationUser> builder)
    {
        builder.Property(u => u.SuspensionReason).HasMaxLength(ApplicationUser.SuspensionReasonMaxLength);

        // Same length as Identity's own NormalizedUserName; looked up when a renamed hero signs in.
        builder.Property(u => u.PreviousNormalizedUserName).HasMaxLength(256);
        builder.HasIndex(u => u.PreviousNormalizedUserName);
    }
}
