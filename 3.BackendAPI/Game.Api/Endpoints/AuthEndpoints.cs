using Game.Api.Auth;
using Game.Api.Hubs;
using Game.Api.Models;
using Game.Core.Entities;
using Game.Infrastructure.Data;
using Game.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Game.Api.Endpoints;

public static class AuthEndpoints
{
    private const int StartingGold = 500;

    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auth")
                       .WithTags("Auth")
                       .AllowAnonymous();

        // Creates the sign-in account and its player profile together, then signs the player in.
        group.MapPost("/register", async (
            RegisterRequest request,
            UserManager<ApplicationUser> userManager,
            AppDbContext dbContext,
            TokenService tokenService,
            RefreshTokenService refreshTokens,
            SessionNotifier notifier) =>
        {
            var username = request.Username?.Trim() ?? string.Empty;
            if (username.Length is < 3 or > 50)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    [nameof(RegisterRequest.Username)] = ["Username must be between 3 and 50 characters."]
                });
            }

            if (await dbContext.Players.AnyAsync(p => p.Username == username))
            {
                return Results.Conflict(new { message = $"The username '{username}' is already taken." });
            }

            var user = new ApplicationUser { Id = Guid.NewGuid(), UserName = username };
            var player = new Player(user.Id, username, StartingGold);

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

            var session = await StartSessionAsync(user, player, tokenService, refreshTokens, dbContext, notifier);
            return Results.Created("/api/players/me", session);
        });

        group.MapPost("/login", async (
            LoginRequest request,
            UserManager<ApplicationUser> userManager,
            SignInManager<ApplicationUser> signInManager,
            AppDbContext dbContext,
            TokenService tokenService,
            RefreshTokenService refreshTokens,
            SessionNotifier notifier) =>
        {
            var user = string.IsNullOrWhiteSpace(request.Username)
                ? null
                : await userManager.FindByNameAsync(request.Username.Trim());

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

            var player = await dbContext.Players.FindAsync(user.Id);
            if (player is null)
            {
                return Results.Problem("This account has no player profile.", statusCode: StatusCodes.Status409Conflict);
            }

            return Results.Ok(await StartSessionAsync(user, player, tokenService, refreshTokens, dbContext, notifier));
        });

        // Trades a refresh token for a new access token and a new refresh token.
        group.MapPost("/refresh", async (
            HttpContext httpContext,
            RefreshRequest request,
            UserManager<ApplicationUser> userManager,
            AppDbContext dbContext,
            TokenService tokenService,
            RefreshTokenService refreshTokens) =>
        {
            if (await refreshTokens.RotateAsync(request.RefreshToken) is not { } rotated)
            {
                return Results.Unauthorized();
            }

            if (rotated.Replacement is null)
            {
                // Tell the browser why, so it can say so instead of "your session expired".
                httpContext.Response.Headers[SessionClaims.EndedHeader] = SessionClaims.SignedInElsewhere;
                return Results.Unauthorized();
            }

            var user = await userManager.FindByIdAsync(rotated.UserId.ToString());
            var player = await dbContext.Players.FindAsync(rotated.UserId);
            if (user is null || player is null || await userManager.IsLockedOutAsync(user))
            {
                return Results.Unauthorized();
            }

            await dbContext.SaveChangesAsync();
            var access = tokenService.CreateAccessToken(user);
            return Results.Ok(new AuthResponse(access.Token, access.ExpiresAt,
                rotated.Replacement.Token, rotated.Replacement.ExpiresAt, PlayerProfileResponse.From(player)));
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
        SessionNotifier notifier)
    {
        // One browser at a time: a new sign-in retires every earlier session's tokens.
        user.CurrentSessionId = Guid.NewGuid();
        await refreshTokens.RevokeAllAsync(user.Id);
        var refresh = refreshTokens.Issue(user.Id);
        await dbContext.SaveChangesAsync();
        await notifier.EndOtherSessionsAsync(user.Id);
        var access = tokenService.CreateAccessToken(user);
        return new AuthResponse(access.Token, access.ExpiresAt, refresh.Token, refresh.ExpiresAt, PlayerProfileResponse.From(player));
    }

    private static Dictionary<string, string[]> ToValidationErrors(IdentityResult result) =>
        result.Errors
            .GroupBy(e => e.Code.StartsWith("Password", StringComparison.Ordinal) ? nameof(RegisterRequest.Password) : nameof(RegisterRequest.Username))
            .ToDictionary(g => g.Key, g => g.Select(e => e.Description).ToArray());
}
