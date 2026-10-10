using Game.Infrastructure.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;

namespace Game.Api.Auth;

/// <summary>
/// The account has two-factor sign-in turned on. The admin tools require it, since an admin account can
/// suspend players and change their gold (see docs/adr/0025-two-factor-sign-in.md).
/// </summary>
public class TwoFactorRequirement : IAuthorizationRequirement;

/// <summary>
/// Reads the account, not the token, so turning two-factor on works at once, and turning it off (or an admin
/// turning it off for a lost phone) shuts the admin tools straight away.
/// </summary>
public class TwoFactorRequirementHandler(UserManager<ApplicationUser> userManager) : AuthorizationHandler<TwoFactorRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, TwoFactorRequirement requirement)
    {
        if (context.User.Identity?.IsAuthenticated != true) return;
        if (await userManager.FindByIdAsync(context.User.GetPlayerId().ToString()) is { TwoFactorEnabled: true })
        {
            context.Succeed(requirement);
        }
    }
}
