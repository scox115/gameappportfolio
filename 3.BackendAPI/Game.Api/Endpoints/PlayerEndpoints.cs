using Game.Api.Models;
using Game.Core.Entities;
using Game.Core.Interfaces;
using Game.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Game.Api.Endpoints;

public static class PlayerEndpoints
{
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

        // GET Endpoint: Fetch a specific player by ID
        group.MapGet("/{id:guid}", async (Guid id, AppDbContext dbContext) =>
        {
            var player = await dbContext.Players.FindAsync(id);

            return player is not null 
                ? Results.Ok(player) 
                : Results.NotFound($"Player with ID {id} was not found.");
        });
    
        // 1. Declare a typed form container at the bottom of the file or in your Models folder


        // 2. Update the Minimal API routing signature
        group.MapPost("/{id:guid}/avatar", async (
            Guid id, 
            [FromForm] AvatarUploadModel model, // Maps the incoming multipart context parameters explicitly
            IStorageService storageService) =>
        {
            // Validate the encapsulated file payload instance safely
            if (model?.File is null || model.File.Length == 0)
            {
                return Results.BadRequest("Invalid file upload package. Ensure an asset is attached.");
            }

            // Open an execution byte read stream on the verified file asset
            using var stream = model.File.OpenReadStream();

            // Stream the asset blocks directly to the local Azurite container storage disk
            var fileUrl = await storageService.UploadFileAsync(stream, model.File.FileName, "player-avatars");

            // Return the successful cloud pointer URL link payload receipt
            return Results.Ok(new { PlayerId = id, AvatarUrl = fileUrl });
        })
        .DisableAntiforgery(); // Bypasses anti-forgery token tracking verification steps for testing
    }
}
