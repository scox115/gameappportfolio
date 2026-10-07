using Game.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Game.Infrastructure.Data.Configurations;

public class AccountTokenConfiguration : IEntityTypeConfiguration<AccountToken>
{
    public void Configure(EntityTypeBuilder<AccountToken> builder)
    {
        builder.ToTable("AccountTokens");

        builder.HasKey(t => t.Id);

        builder.Property(t => t.Purpose).HasConversion<string>().HasMaxLength(20);
        builder.Property(t => t.TokenHash).HasMaxLength(64).IsRequired();
        builder.Property(t => t.Email).HasMaxLength(AccountToken.EmailMaxLength);

        builder.HasIndex(t => t.TokenHash).IsUnique();
        builder.HasIndex(t => new { t.UserId, t.Purpose, t.CreatedAt });
        builder.HasIndex(t => t.ExpiresAt); // for the cleanup worker

        // Deleting an account deletes its links.
        builder.HasOne<ApplicationUser>().WithMany().HasForeignKey(t => t.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}
