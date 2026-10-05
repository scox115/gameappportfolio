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

        // POST Endpoint: Create a New Player
        group.MapPost("/", async (CreatePlayerRequest request, AppDbContext dbContext) =>
        {
            // 1. Validation check
            if (string.IsNullOrWhiteSpace(request.Username))
            {
                return Results.BadRequest("Username cannot be empty.");
            }

            // 2. Check if username is already taken (handling unique constraint gracefully)
            bool usernameExists = await dbContext.Players
                .AnyAsync(p => p.Username == request.Username);

            if (usernameExists)
            {
                return Results.Conflict($"The username '{request.Username}' is already taken.");
            }

            // 3. Construct our Rich Domain Entity (starting with 500 gold)
            var newPlayer = new Player(request.Username, startingGold: 500);

            // 4. Save to database using EF Core
            dbContext.Players.Add(newPlayer);
            await dbContext.SaveChangesAsync();

            // 5. Return a 201 Created status containing the unique route to locate the resource
            return Results.Created($"/api/players/{newPlayer.Id}", new 
            {
                id = newPlayer.Id,
                username = newPlayer.Username,
                gold = newPlayer.Gold,
                level = newPlayer.Level,
                experiencePoints = newPlayer.ExperiencePoints
            });
        });

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
        group.MapPost("/{id:guid}/avatar", async (
            Guid id, 
            [FromForm] AvatarUploadModel model, 
            AppDbContext dbContext,
            IStorageService storageService) =>
        {
            // Check if the player exists in the SQL database first
            var player = await dbContext.Players.FindAsync(id);
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

            return Results.Ok(new { PlayerId = id, AvatarUrl = uploadedBlobUrl });
        })
        .DisableAntiforgery();

        // POST: /api/players/login
        group.MapPost("/login", async (LoginRequest request, AppDbContext dbContext) =>
        {
            if (string.IsNullOrWhiteSpace(request.Username))
            {
                return Results.BadRequest("Username field cannot be left blank.");
            }

            // Use EF.Functions.Like to enforce a true case-insensitive lookup inside SQL Server
            var player = await dbContext.Players
                .FirstOrDefaultAsync(p => EF.Functions.Like(p.Username, request.Username));

            if (player is null)
            {
                return Results.NotFound($"No character profile named '{request.Username}' was found.");
            }

            // Return the response data contract with exact property casing matching your Blazor DTOs
            return Results.Ok(new 
            {
                id = player.Id,
                username = player.Username,
                gold = player.Gold,
                level = player.Level,
                avatarUrl = player.AvatarUrl
            });
        });
    }
}

public record LoginRequest(string Username);