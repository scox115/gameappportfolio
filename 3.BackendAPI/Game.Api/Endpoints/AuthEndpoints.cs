using Game.Api.Admin;
using Game.Api.Auth;
using Game.Api.Hubs;
using Game.Api.Models;
using Game.Api.Moderation;
using Game.Api.Options;
using Game.Core.Entities;
using Game.Core.Moderation;
using Game.Infrastructure.Data;
using Game.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Game.Api.Endpoints;

public static class AuthEndpoints
{

    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/auth")
                       .WithTags("Auth")
                       .AllowAnonymous();

        // Creates the sign-in account and its player profile together, then signs the player in.
        group.MapPost("/register", async (
            RegisterRequest request,
            UserManager<ApplicationUser> userManager,
            AppDbContext dbContext,
            TokenService tokenService,
            RefreshTokenService refreshTokens,
            SessionNotifier notifier,
            AdminRoleSync roleSync,
            ModerationService moderation,
            IOptions<AdminOptions> adminOptions) =>
        {
            var username = request.Username?.Trim() ?? string.Empty;
            if (HeroNames.Problem(username, allowStaffNames: adminOptions.Value.IsAdmin(username)) is { } nameProblem)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    [nameof(RegisterRequest.Username)] = [nameProblem]
                });
            }

            if (request.Class is { } chosen && !Enum.IsDefined(chosen))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    [nameof(RegisterRequest.Class)] = ["Unknown class."]
                });
            }

            // A name an admin took away stays reserved for the hero who had it (they can still sign in with it).
            if (await dbContext.Players.AnyAsync(p => p.Username == username)
                || await moderation.NameTakenAsync(userManager.NormalizeName(username), except: null))
            {
                return Results.Conflict(new { message = $"The username '{username}' is already taken." });
            }

            var user = new ApplicationUser { Id = Guid.NewGuid(), UserName = username };
            var player = new Player(user.Id, username, Player.StartingGold);
            if (request.Class is { } heroClass) player.ChooseStartingClass(heroClass);

            // The player is tracked on the same DbContext the Identity store saves with,
            // so the account and the profile are written in one SaveChanges call.
            dbContext.Players.Add(player);
            var result = await userManager.CreateAsync(user, request.Password ?? string.Empty);
            if (!result.Succeeded)
            {
                dbContext.Entry(player).State = EntityState.Detached;
                return result.Errors.Any(e => e.Code == nameof(IdentityErrorDescriber.DuplicateUserName))
                    ? Results.Conflict(new { message = $"The username '{username}' is already taken." })
                    : Results.ValidationProblem(ToValidationErrors(result));
            }

            var session = await StartSessionAsync(user, player, tokenService, refreshTokens, dbContext, notifier, roleSync);
            return Results.Created("/players/me", session);
        }).RequireRateLimiting(RateLimits.Registration);

        group.MapPost("/login", async (
            LoginRequest request,
            UserManager<ApplicationUser> userManager,
            SignInManager<ApplicationUser> signInManager,
            AppDbContext dbContext,
            TokenService tokenService,
            RefreshTokenService refreshTokens,
            SessionNotifier notifier,
            AdminRoleSync roleSync,
            TimeProvider timeProvider) =>
        {
            var user = string.IsNullOrWhiteSpace(request.Username)
                ? null
                : await userManager.FindByNameAsync(request.Username.Trim());

            // A hero an admin renamed can still sign in with their old name; the response carries the new one.
            if (user is null && !string.IsNullOrWhiteSpace(request.Username))
            {
                var previous = userManager.NormalizeName(request.Username.Trim());
                user = await dbContext.Users.FirstOrDefaultAsync(u => u.PreviousNormalizedUserName == previous);
            }

            // Same response for an unknown user and a wrong password, so usernames can't be probed.
            if (user is null)
            {
                return Results.Unauthorized();
            }

            var signIn = await signInManager.CheckPasswordSignInAsync(user, request.Password ?? string.Empty, lockoutOnFailure: true);
            if (signIn.IsLockedOut)
            {
                return Results.Problem("Too many failed sign-in attempts. Try again later.", statusCode: StatusCodes.Status423Locked);
            }

            if (!signIn.Succeeded)
            {
                return Results.Unauthorized();
            }

            // Only said once the password is right, so the reason isn't shown to anyone who knows the name.
            if (user.IsSuspended(timeProvider.GetUtcNow()))
            {
                return Results.Problem(SuspendedMessage(user), statusCode: StatusCodes.Status403Forbidden, title: "Suspended",
                    extensions: new Dictionary<string, object?> { ["suspendedUntil"] = user.SuspendedUntil });
            }

            var player = await dbContext.Players.FindAsync(user.Id);
            if (player is null)
            {
                return Results.Problem("This account has no player profile.", statusCode: StatusCodes.Status409Conflict);
            }

            return Results.Ok(await StartSessionAsync(user, player, tokenService, refreshTokens, dbContext, notifier, roleSync));
        }).RequireRateLimiting(RateLimits.SignIn);

        // Trades a refresh token for a new access token and a new refresh token.
        group.MapPost("/refresh", async (
            HttpContext httpContext,
            RefreshRequest request,
            UserManager<ApplicationUser> userManager,
            AppDbContext dbContext,
            TokenService tokenService,
            RefreshTokenService refreshTokens,
            ActiveSessionValidator sessions) =>
        {
            if (await refreshTokens.RotateAsync(request.RefreshToken) is not { } rotated)
            {
                return Results.Unauthorized();
            }

            if (rotated.Replacement is null)
            {
                // Tell the browser why, so it can say so instead of "your session expired".
                httpContext.Response.Headers[SessionClaims.EndedHeader] = await sessions.EndReasonAsync(rotated.UserId);
                return Results.Unauthorized();
            }

            var user = await userManager.FindByIdAsync(rotated.UserId.ToString());
            var player = await dbContext.Players.FindAsync(rotated.UserId);
            if (user is null || player is null || await userManager.IsLockedOutAsync(user))
            {
                return Results.Unauthorized();
            }

            await dbContext.SaveChangesAsync();
            var roles = await userManager.GetRolesAsync(user);
            var access = tokenService.CreateAccessToken(user, roles);
            return Results.Ok(new AuthResponse(access.Token, access.ExpiresAt,
                rotated.Replacement.Token, rotated.Replacement.ExpiresAt, PlayerProfileResponse.From(player), roles.ToList()));
        });

        // Signing out revokes the refresh token so it can't be used again.
        group.MapPost("/logout", async (RefreshRequest request, RefreshTokenService refreshTokens) =>
        {
            await refreshTokens.RevokeAsync(request.RefreshToken);
            return Results.NoContent();
        });
    }

    private static async Task<AuthResponse> StartSessionAsync(
        ApplicationUser user,
        Player player,
        TokenService tokenService,
        RefreshTokenService refreshTokens,
        AppDbContext dbContext,
        SessionNotifier notifier,
        AdminRoleSync roleSync)
    {
        var roles = await roleSync.SyncAsync(user);

        // One browser at a time: a new sign-in retires every earlier session's tokens.
        user.CurrentSessionId = Guid.NewGuid();
        await refreshTokens.RevokeAllAsync(user.Id);
        var refresh = refreshTokens.Issue(user.Id);
        await dbContext.SaveChangesAsync();
        await notifier.EndOtherSessionsAsync(user.Id);
        var access = tokenService.CreateAccessToken(user, roles);
        return new AuthResponse(access.Token, access.ExpiresAt, refresh.Token, refresh.ExpiresAt, PlayerProfileResponse.From(player), roles.ToList());
    }

    private static string SuspendedMessage(ApplicationUser user)
    {
        var until = user.SuspendedUntil == DateTimeOffset.MaxValue
            ? "until an admin reinstates it"
            : $"until {AdminService.Describe(user.SuspendedUntil!.Value)}";
        return $"This hero is suspended {until}. Reason: {user.SuspensionReason}";
    }

    private static Dictionary<string, string[]> ToValidationErrors(IdentityResult result) =>
        result.Errors
            .GroupBy(e => e.Code.StartsWith("Password", StringComparison.Ordinal) ? nameof(RegisterRequest.Password) : nameof(RegisterRequest.Username))
            .ToDictionary(g => g.Key, g => g.Select(e => e.Description).ToArray());
}
