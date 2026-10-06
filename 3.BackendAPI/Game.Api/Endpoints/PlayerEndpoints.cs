using System.Security.Claims;
using Game.Api.Auth;
using Game.Api.Models;
using Game.Core.Entities;
using Game.Core.Interfaces;
using Game.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc;

namespace Game.Api.Endpoints;

public static class PlayerEndpoints
{
    // TODO: move this
    // 1. Declare a typed form container at the bottom of the file or in your Models folder
    private class AvatarUploadModel
    {
        [FromForm]
        public IFormFile File { get; set; } = null!;
    }

    public static void MapPlayerEndpoints(this IEndpointRouteBuilder app)
    {
        // Group all player routes under a standard API prefix path
        var group = app.MapGroup("/api/players")
                       .WithTags("Players"); // Categories routes cleanly in Swagger

        // GET Endpoint: The signed-in player's own profile
        group.MapGet("/me", async (ClaimsPrincipal user, AppDbContext dbContext) =>
        {
            var player = await dbContext.Players.FindAsync(user.GetPlayerId());

            return player is not null
                ? Results.Ok(PlayerProfileResponse.From(player))
                : Results.NotFound("Player profile not found.");
        })
        .RequireAuthorization();

        // GET Endpoint: Retrieve a Player by ID
        group.MapGet("/{id:guid}", async (Guid id, AppDbContext dbContext) =>
        {
            var player = await dbContext.Players.FindAsync(id);

            return player is not null 
                ? Results.Ok(new 
                {
                    id = player.Id,
                    username = player.Username,
                    gold = player.Gold,
                    level = player.Level,
                    experiencePoints = player.ExperiencePoints,
                    avatarUrl = player.AvatarUrl // --- INCLUDE THIS FIELD IN THE JSON RETURN ---
                }) 
                : Results.NotFound($"Player with ID {id} was not found.");
        });
            
        // POST Endpoint: Upload a Player Avatar
        // Players can only change their own avatar, so the id comes from the access token.
        group.MapPost("/me/avatar", async (
            ClaimsPrincipal user,
            [FromForm] AvatarUploadModel model, 
            AppDbContext dbContext,
            IStorageService storageService) =>
        {
            // Check if the player exists in the SQL database first
            var player = await dbContext.Players.FindAsync(user.GetPlayerId());
            if (player is null) 
            {
                return Results.NotFound("Player profile not found.");
            }

            // Double check that a physical file was attached
            if (model?.File is null || model.File.Length == 0)
            {
                return Results.BadRequest("Invalid upload request. File content is missing.");
            }

            // --- 🚀 THE CRITICAL PIECE: OPEN THE ACTUAL FILE FILE STREAM ---
            // This reads the raw binary image bytes from the browser upload request
            using var stream = model.File.OpenReadStream();

            // Pass the raw byte stream and file name to the Azurite SDK client
            // This physically saves the image inside your active Azurite Docker container
            var uploadedBlobUrl = await storageService.UploadFileAsync(stream, model.File.FileName, "player-avatars");

            // Save the resulting cloud address string back to our SQL database row
            player.UpdateAvatar(uploadedBlobUrl);
            await dbContext.SaveChangesAsync();

            return Results.Ok(new { PlayerId = player.Id, AvatarUrl = uploadedBlobUrl });
        })
        .RequireAuthorization()
        .DisableAntiforgery();

        // GET: /api/players/leaderboard
        group.MapGet("/leaderboard", async (AppDbContext dbContext) =>
        {
            // Top 10 by PvP rating; level and experience break ties (such as players who haven't dueled yet)
            var topPlayers = await dbContext.Players
                .OrderByDescending(p => p.Rating)
                .ThenByDescending(p => p.Level)
                .ThenByDescending(p => p.ExperiencePoints)
                .Take(10)
                .Select(p => new { p.Id, p.Username, p.Level, p.Gold, p.AvatarUrl, p.EquippedTitle, p.PvpWins, p.PvpLosses, p.Rating, p.EquippedFrame })
                .ToListAsync();

            return Results.Ok(topPlayers.Select(p => new
            {
                id = p.Id,
                username = p.Username,
                title = p.EquippedTitle is { } title ? PlayerTitles.Get(title).Name : null,
                level = p.Level,
                gold = p.Gold,
                rating = p.Rating,
                pvpWins = p.PvpWins,
                pvpLosses = p.PvpLosses,
                avatarUrl = p.AvatarUrl,
                frame = p.EquippedFrame
            }));
        });
    }
}