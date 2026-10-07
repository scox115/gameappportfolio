using Game.Api.Auth;
using Game.Api.Options;
using Game.Core.Admin;
using Game.Infrastructure.Data;
using Game.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace Game.Api.Admin;

/// <summary>
/// Keeps the Admin role in step with Admin:Usernames. It runs at every sign-in, so adding a name to
/// the setting makes that account an admin the next time it signs in, and taking it out removes the
/// role the same way. Both changes go in the audit log.
/// </summary>
public class AdminRoleSync(
    UserManager<ApplicationUser> userManager,
    RoleManager<IdentityRole<Guid>> roleManager,
    AppDbContext dbContext,
    IOptions<AdminOptions> options,
    TimeProvider timeProvider,
    ILogger<AdminRoleSync> logger)
{
    /// <summary>Grants or removes the Admin role to match the setting, and returns the account's roles.</summary>
    public async Task<IList<string>> SyncAsync(ApplicationUser user)
    {
        var listed = options.Value.IsAdmin(user.UserName);
        var isAdmin = await userManager.IsInRoleAsync(user, GameRoles.Admin);

        if (listed && !isAdmin)
        {
            if (!await roleManager.RoleExistsAsync(GameRoles.Admin))
            {
                await roleManager.CreateAsync(new IdentityRole<Guid>(GameRoles.Admin) { Id = Guid.NewGuid() });
            }

            Record(user, AdminAction.GrantAdmin, $"Listed in {AdminOptions.SectionName}:{nameof(AdminOptions.Usernames)}.");
            await Check(userManager.AddToRoleAsync(user, GameRoles.Admin)); // saves the audit entry too
            logger.LogWarning("{Username} is now an admin, from configuration.", user.UserName);
        }
        else if (!listed && isAdmin)
        {
            Record(user, AdminAction.RevokeAdmin, $"No longer listed in {AdminOptions.SectionName}:{nameof(AdminOptions.Usernames)}.");
            await Check(userManager.RemoveFromRoleAsync(user, GameRoles.Admin));
            logger.LogWarning("{Username} is no longer an admin, from configuration.", user.UserName);
        }

        return await userManager.GetRolesAsync(user);
    }

    private void Record(ApplicationUser user, AdminAction action, string reason) =>
        dbContext.AuditLog.Add(new AuditLogEntry(
            timeProvider.GetUtcNow().UtcDateTime, action, Guid.Empty, AuditLogEntry.ConfigurationActor,
            user.Id, user.UserName ?? user.Id.ToString(), reason));

    private static async Task Check(Task<IdentityResult> change)
    {
        var result = await change;
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                $"Couldn't update the Admin role: {string.Join(" ", result.Errors.Select(e => e.Description))}");
        }
    }
}
