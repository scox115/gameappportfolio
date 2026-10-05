using Game.Api.Auth;
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
            TokenService tokenService) =>
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

            var token = tokenService.CreateAccessToken(user);
            return Results.Created("/api/players/me",
                new AuthResponse(token.Token, token.ExpiresAt, PlayerProfileResponse.From(player)));
        });

        group.MapPost("/login", async (
            LoginRequest request,
            UserManager<ApplicationUser> userManager,
            SignInManager<ApplicationUser> signInManager,
            AppDbContext dbContext,
            TokenService tokenService) =>
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

            var token = tokenService.CreateAccessToken(user);
            return Results.Ok(new AuthResponse(token.Token, token.ExpiresAt, PlayerProfileResponse.From(player)));
        });
    }

    private static Dictionary<string, string[]> ToValidationErrors(IdentityResult result) =>
        result.Errors
            .GroupBy(e => e.Code.StartsWith("Password", StringComparison.Ordinal) ? nameof(RegisterRequest.Password) : nameof(RegisterRequest.Username))
            .ToDictionary(g => g.Key, g => g.Select(e => e.Description).ToArray());
}
