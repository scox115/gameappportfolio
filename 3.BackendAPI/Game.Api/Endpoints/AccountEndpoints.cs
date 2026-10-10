using System.Security.Claims;
using System.Text.Json;
using Game.Api.Accounts;
using Game.Api.Auth;
using Game.Api.Models;
using Game.Api.Moderation;
using Game.Api.Options;
using Game.Api.Recovery;
using Game.Core.Moderation;
using Game.Infrastructure.Data;
using Game.Infrastructure.Identity;
using HttpJsonOptions = Microsoft.AspNetCore.Http.Json.JsonOptions;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Game.Api.Endpoints;

public record DeleteAccountRequest(string? Password);

// The player's own data: download a copy of it, or delete the account and everything with it.
public static class AccountEndpoints
{
    public static void MapAccountEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/players/me")
                       .WithTags("Players")
                       .RequireAuthorization();

        // GET: /api/players/me/export, a JSON file of everything stored about the player
        group.MapGet("/export", async (ClaimsPrincipal user, AccountService accounts, IOptions<HttpJsonOptions> json, CancellationToken cancellationToken) =>
        {
            if (await accounts.ExportAsync(user.GetPlayerId(), cancellationToken) is not { } export)
            {
                return Results.NotFound("Player profile not found.");
            }

            var options = new JsonSerializerOptions(json.Value.SerializerOptions) { WriteIndented = true };
            var file = JsonSerializer.SerializeToUtf8Bytes(export, options);
            return Results.File(file, "application/json", $"card-arena-{export.Account.Username}-{export.ExportedAt:yyyy-MM-dd}.json");
        });

        // POST: /api/players/me/keep, a guest keeps their hero under a name and password of their own
        group.MapPost("/keep", async (
            ClaimsPrincipal user,
            KeepGuestRequest request,
            UserManager<ApplicationUser> userManager,
            AppDbContext dbContext,
            ModerationService moderation,
            IOptions<AdminOptions> adminOptions,
            AccountRecoveryService recovery) =>
        {
            var account = await userManager.FindByIdAsync(user.GetPlayerId().ToString());
            var player = await dbContext.Players.FindAsync(user.GetPlayerId());
            if (account is null || player is null) return Results.NotFound("Player profile not found.");
            if (!player.IsGuest) return Results.Conflict(new { message = "This hero is already saved." });

            var username = request.Username?.Trim() ?? string.Empty;
            if (HeroNames.Problem(username, allowStaffNames: adminOptions.Value.IsAdmin(username)) is { } nameProblem)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { [nameof(KeepGuestRequest.Username)] = [nameProblem] });
            }

            // As at sign-up, the recovery email is optional, and ignored where email isn't set up.
            var recoveryEmail = recovery.OptionalAddress(request.RecoveryEmail);
            if (recoveryEmail is not null && AccountRecoveryService.Normalize(recoveryEmail) is null)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { [nameof(KeepGuestRequest.RecoveryEmail)] = [AccountRecoveryService.NotAnAddress] });
            }

            if (await dbContext.Players.AnyAsync(p => p.Username == username && p.Id != player.Id)
                || await moderation.NameTakenAsync(userManager.NormalizeName(username), except: null))
            {
                return Results.Conflict(new { message = $"The username '{username}' is already taken." });
            }

            // The name, the password and the hero are saved together: AddPasswordAsync checks the password
            // rules first and saves nothing if they fail.
            player.KeepAs(username);
            account.UserName = username;
            // Opponents' match history shows the new name, not the made-up one.
            foreach (var entry in await dbContext.MatchHistory.Where(e => e.OpponentId == player.Id).ToListAsync())
            {
                entry.RenameOpponent(username);
            }
            var result = await userManager.AddPasswordAsync(account, request.Password ?? string.Empty);
            if (!result.Succeeded)
            {
                return result.Errors.Any(e => e.Code == nameof(IdentityErrorDescriber.DuplicateUserName))
                    ? Results.Conflict(new { message = $"The username '{username}' is already taken." })
                    : Results.ValidationProblem(result.Errors
                        .GroupBy(e => e.Code.StartsWith("Password", StringComparison.Ordinal) ? nameof(KeepGuestRequest.Password) : nameof(KeepGuestRequest.Username))
                        .ToDictionary(g => g.Key, g => g.Select(e => e.Description).ToArray()));
            }

            if (recoveryEmail is not null) await recovery.OfferConfirmationAsync(account, recoveryEmail);
            return Results.Ok(PlayerProfileResponse.From(player));
        });

        // DELETE: /api/players/me, with the password, since this can't be undone
        group.MapDelete("", async (
            ClaimsPrincipal user,
            [FromBody] DeleteAccountRequest request,
            UserManager<ApplicationUser> userManager,
            SignInManager<ApplicationUser> signInManager,
            AccountService accounts,
            CancellationToken cancellationToken) =>
        {
            var account = await userManager.FindByIdAsync(user.GetPlayerId().ToString());
            if (account is null)
            {
                return Results.NotFound("Player profile not found.");
            }

            // Counts toward the lockout like a sign-in, so a stolen session can't guess its way to a delete.
            var check = await signInManager.CheckPasswordSignInAsync(account, request.Password ?? string.Empty, lockoutOnFailure: true);
            if (check.IsLockedOut)
            {
                return Results.Problem("Too many failed attempts. Try again later.", statusCode: StatusCodes.Status423Locked);
            }

            if (!check.Succeeded)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    [nameof(DeleteAccountRequest.Password)] = ["That password isn't right."]
                });
            }

            return await accounts.DeleteAsync(account.Id, cancellationToken) switch
            {
                DeletionResult.Deleted => Results.NoContent(),
                DeletionResult.InDuel => Results.Problem("Finish or forfeit your duel first.", statusCode: StatusCodes.Status409Conflict),
                _ => Results.NotFound("Player profile not found."),
            };
        })
        .RequireRateLimiting(RateLimits.SignIn);
    }
}
