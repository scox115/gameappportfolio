using System.Security.Claims;
using Game.Api.Auth;
using Game.Api.Caching;
using Game.Api.Models;
using Game.Core.Battles;
using Game.Core.Entities;
using Game.Core.Interfaces;
using Game.Core.Media;
using Game.Core.Moderation;
using Game.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc;

namespace Game.Api.Endpoints;

public static class PlayerEndpoints
{
    /// <summary>The blob container portraits are uploaded to (the blob storage health check reads it too).</summary>
    public const string AvatarContainer = "player-avatars";

    private static IResult AvatarProblem(string message) =>
        Results.ValidationProblem(new Dictionary<string, string[]> { ["File"] = [message] });

    private class AvatarUploadModel
    {
        [FromForm]
        public IFormFile File { get; set; } = null!;
    }

    public static void MapPlayerEndpoints(this IEndpointRouteBuilder app)
    {
        // Group all player routes under a standard API prefix path
        var group = app.MapGroup("/players")
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
            IStorageService storageService,
            IPortraitScreen portraitScreen,
            ILoggerFactory loggerFactory,
            CancellationToken cancellationToken) =>
        {
            // Check if the player exists in the SQL database first
            var player = await dbContext.Players.FindAsync(user.GetPlayerId());
            if (player is null) 
            {
                return Results.NotFound("Player profile not found.");
            }

            if (model?.File is null || model.File.Length == 0)
            {
                return AvatarProblem("Choose a PNG or JPG image to upload.");
            }

            if (model.File.Length > AvatarImage.MaxBytes)
            {
                return AvatarProblem("Portraits must be 4 MB or smaller.");
            }

            // Read the upload (at most 4 MB) and decide its type from the bytes themselves. The file
            // name and the browser's Content-Type are both up to the client, so neither is trusted.
            using var content = new MemoryStream((int)model.File.Length);
            await model.File.CopyToAsync(content);
            var format = AvatarImage.Detect(content.GetBuffer().AsSpan(0, (int)content.Length));
            if (format is null)
            {
                return AvatarProblem("Portraits must be PNG or JPG images.");
            }

            // Checked before it's stored, so nobody else ever sees a portrait that fails.
            var screening = await portraitScreen.ScreenAsync(content.GetBuffer().AsMemory(0, (int)content.Length), cancellationToken);
            switch (screening.Verdict)
            {
                case PortraitVerdict.Blocked:
                    loggerFactory.CreateLogger("Game.Api.Avatars")
                        .LogWarning("Turned away a portrait from player {PlayerId} for {Harm}.", player.Id, screening.Harm);
                    return AvatarProblem(screening.Explanation!);
                case PortraitVerdict.Unreadable:
                    return AvatarProblem(screening.Explanation!);
                case PortraitVerdict.Unavailable:
                    return Results.Problem(screening.Explanation, statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            content.Position = 0;
            var uploadedBlobUrl = await storageService.UploadFileAsync(content, "avatar" + format.Extension, AvatarContainer, format.ContentType);

            var previousUrl = player.AvatarUrl;
            player.UpdateAvatar(uploadedBlobUrl);
            await dbContext.SaveChangesAsync();

            // The old portrait is no longer shown anywhere, so don't keep paying to store it.
            if (previousUrl is not null)
            {
                try
                {
                    await storageService.DeleteFileAsync(previousUrl, AvatarContainer);
                }
                catch (Exception ex)
                {
                    loggerFactory.CreateLogger("Game.Api.Avatars")
                        .LogWarning("Couldn't delete the old portrait for player {PlayerId}: {Error}", player.Id, ex.Message);
                }
            }

            return Results.Ok(new { PlayerId = player.Id, AvatarUrl = uploadedBlobUrl });
        })
        .RequireAuthorization()
        .RequireRateLimiting(RateLimits.AvatarUpload)
        // Turn away oversized bodies before they're read, leaving room for the multipart framing.
        .WithMetadata(new RequestSizeLimitAttribute(AvatarImage.MaxBytes + 64 * 1024))
        .DisableAntiforgery();

        // GET: /api/players/stats
        group.MapGet("/stats", async (AppDbContext dbContext) =>
            Results.Ok(new PlayerStatsResponse(await dbContext.Players.CountAsync())))
            .CacheOutput(OutputCaching.Policies.PlayerCount);

        // GET: /api/players/leaderboard[?class=Paladin]
        group.MapGet("/leaderboard", async (AppDbContext dbContext, HeroClass? @class) =>
        {
            if (@class is { } heroClass)
            {
                if (!Enum.IsDefined(heroClass))
                {
                    return Results.ValidationProblem(new Dictionary<string, string[]>
                    {
                        ["class"] = ["Unknown class."]
                    });
                }

                // Top 10 duelists as this class, by wins then fewest losses; rating breaks ties.
                var classLeaders = await dbContext.Players
                    .Where(p => p.GuestSince == null) // guests try the game; leaderboards are for heroes people kept
                    .SelectMany(p => p.ClassRecords
                        .Where(r => r.Class == heroClass && r.Wins + r.Losses > 0)
                        .Select(r => new { Player = p, r.Wins, r.Losses }))
                    .OrderByDescending(x => x.Wins)
                    .ThenBy(x => x.Losses)
                    .ThenByDescending(x => x.Player.Rating)
                    .Take(10)
                    .Select(x => new
                    {
                        x.Player.Id, x.Player.Username, x.Player.Level, x.Player.Gold, x.Player.AvatarUrl,
                        x.Player.EquippedTitle, PvpWins = x.Wins, PvpLosses = x.Losses, x.Player.Rating,
                        x.Player.EquippedFrame, x.Player.Class
                    })
                    .ToListAsync();

                return Results.Ok(classLeaders.Select(p => LeaderboardRow(
                    p.Id, p.Username, p.EquippedTitle, p.Level, p.Gold, p.Rating, p.PvpWins, p.PvpLosses,
                    p.AvatarUrl, p.EquippedFrame, p.Class)));
            }

            // Top 10 by PvP rating; level and experience break ties (such as players who haven't dueled yet)
            var topPlayers = await dbContext.Players
                .Where(p => p.GuestSince == null)
                .OrderByDescending(p => p.Rating)
                .ThenByDescending(p => p.Level)
                .ThenByDescending(p => p.ExperiencePoints)
                .Take(10)
                .Select(p => new { p.Id, p.Username, p.Level, p.Gold, p.AvatarUrl, p.EquippedTitle, p.PvpWins, p.PvpLosses, p.Rating, p.EquippedFrame, p.Class })
                .ToListAsync();

            return Results.Ok(topPlayers.Select(p => LeaderboardRow(
                p.Id, p.Username, p.EquippedTitle, p.Level, p.Gold, p.Rating, p.PvpWins, p.PvpLosses,
                p.AvatarUrl, p.EquippedFrame, p.Class)));
        })
        .CacheOutput(OutputCaching.Policies.Leaderboard);
    }

    private static object LeaderboardRow(Guid id, string username, PlayerTitle? title, int level, int gold, int rating,
        int pvpWins, int pvpLosses, string? avatarUrl, Cosmetic? frame, HeroClass heroClass) => new
    {
        id,
        username,
        title = title is { } t ? PlayerTitles.Get(t).Name : null,
        level,
        gold,
        rating,
        pvpWins,
        pvpLosses,
        avatarUrl,
        frame,
        heroClass = HeroClasses.Get(heroClass).Name
    };

}