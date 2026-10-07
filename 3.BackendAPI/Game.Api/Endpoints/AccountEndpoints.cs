using System.Security.Claims;
using System.Text.Json;
using Game.Api.Accounts;
using Game.Api.Auth;
using Game.Infrastructure.Identity;
using HttpJsonOptions = Microsoft.AspNetCore.Http.Json.JsonOptions;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
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
