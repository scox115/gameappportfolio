using System.Security.Claims;
using Game.Api.Auth;
using Game.Api.Caching;
using Game.Api.Models;
using Game.Api.Seasons;
using Game.Core.Battles;
using Game.Core.Entities;
using Game.Core.Seasons;
using Game.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Game.Api.Endpoints;

/// <summary>Monthly ranked seasons: the season under way, past seasons' final standings, and the signed-in hero's season.</summary>
public static class SeasonEndpoints
{
    private const int StandingsShown = 10;

    public static void MapSeasonEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/seasons").WithTags("Seasons");

        // GET: /api/v1/seasons
        group.MapGet("/", async (AppDbContext dbContext, SeasonService seasons) =>
        {
            var past = await dbContext.ClosedSeasons
                .OrderByDescending(s => s.SeasonStart)
                .Take(12)
                .ToListAsync();

            return Results.Ok(new SeasonsResponse(
                SeasonView.From(seasons.Current),
                SeasonRules.DuelsToBeRanked,
                [new("1st", SeasonRules.RewardFor(1)), new("2nd-3rd", SeasonRules.RewardFor(2)),
                 new("4th-10th", SeasonRules.RewardFor(4)), new("11th and below", SeasonRules.RewardFor(11))],
                past.Select(s => new PastSeasonView(s.Season.Key, s.Season.Name, s.RankedHeroes)).ToList()));
        })
        .CacheOutput(OutputCaching.Policies.Leaderboard);

        // GET: /api/v1/seasons/2026-10/standings
        group.MapGet("/{key}/standings", async (string key, AppDbContext dbContext, SeasonService seasons) =>
        {
            if (!Season.TryParse(key, out var season))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["key"] = ["Seasons are named like 2026-10."] });
            }

            var start = season.Start;
            if (season == seasons.Current)
            {
                // Live: everyone who has fought enough duels this season, by rating.
                var leaders = await dbContext.Players
                    .Where(p => p.SeasonStart == start && p.SeasonWins + p.SeasonLosses >= SeasonRules.DuelsToBeRanked)
                    .Where(p => p.GuestSince == null && p.Id != ArenaBot.Id)
                    .OrderByDescending(p => p.Rating)
                    .ThenByDescending(p => p.SeasonWins)
                    .ThenBy(p => p.SeasonLosses)
                    .ThenBy(p => p.Username)
                    .Take(StandingsShown)
                    .Select(p => new { p.Id, p.Username, p.EquippedTitle, p.Class, p.AvatarUrl, p.EquippedFrame, p.Rating, Wins = p.SeasonWins, Losses = p.SeasonLosses })
                    .ToListAsync();

                return Results.Ok(new SeasonStandingsResponse(SeasonView.From(season), Final: false,
                    leaders.Select((p, i) => Row(i + 1, p.Id, p.Username, p.EquippedTitle, p.Class, p.AvatarUrl, p.EquippedFrame,
                        p.Rating, p.Wins, p.Losses, SeasonRules.RewardFor(i + 1))).ToList()));
            }

            if (!await dbContext.ClosedSeasons.AnyAsync(s => s.SeasonStart == start))
            {
                return Results.NotFound();
            }

            var finishers = await dbContext.Players
                .SelectMany(p => p.SeasonRecords
                    .Where(r => r.SeasonStart == start && r.Rank != null)
                    .Select(r => new
                    {
                        Rank = r.Rank!.Value, p.Id, p.Username, p.EquippedTitle, p.Class, p.AvatarUrl, p.EquippedFrame,
                        r.Rating, r.Wins, r.Losses, r.RewardGold
                    }))
                .OrderBy(x => x.Rank)
                .Take(StandingsShown)
                .ToListAsync();

            return Results.Ok(new SeasonStandingsResponse(SeasonView.From(season), Final: true,
                finishers.Select(p => Row(p.Rank, p.Id, p.Username, p.EquippedTitle, p.Class, p.AvatarUrl, p.EquippedFrame,
                    p.Rating, p.Wins, p.Losses, p.RewardGold)).ToList()));
        })
        .CacheOutput(OutputCaching.Policies.Leaderboard);

        // GET: /api/v1/seasons/me
        group.MapGet("/me", async (ClaimsPrincipal user, AppDbContext dbContext, SeasonService seasons) =>
        {
            var player = await dbContext.Players.FindAsync(user.GetPlayerId());
            if (player is null)
            {
                return Results.NotFound("Player profile not found.");
            }

            var current = seasons.Current;
            var inSeason = player.SeasonStart == current.Start;
            // Until the season worker catches up, show the rating the hero will start this season with.
            var rating = inSeason || player.SeasonStart is null ? player.Rating : SeasonRules.SoftReset(player.Rating);
            var wins = inSeason ? player.SeasonWins : 0;
            var losses = inSeason ? player.SeasonLosses : 0;

            int? rank = null;
            if (inSeason && wins + losses >= SeasonRules.DuelsToBeRanked && SeasonService.Ranks(player))
            {
                var start = current.Start;
                rank = 1 + await dbContext.Players.CountAsync(p =>
                    p.SeasonStart == start && p.SeasonWins + p.SeasonLosses >= SeasonRules.DuelsToBeRanked
                    && p.GuestSince == null && p.Id != ArenaBot.Id && p.Rating > player.Rating);
            }

            var last = player.SeasonRecords.Where(r => r.Settled).MaxBy(r => r.SeasonStart);
            return Results.Ok(new MySeasonResponse(SeasonView.From(current), rating, wins, losses, SeasonRules.DuelsToBeRanked, rank,
                last is null ? null : MySeasonResultView.From(last)));
        })
        .RequireAuthorization();
    }

    private static SeasonStandingRow Row(int rank, Guid id, string username, PlayerTitle? title, HeroClass heroClass, string? avatarUrl,
        Cosmetic? frame, int rating, int wins, int losses, int rewardGold) =>
        new(rank, id, username, title is { } t ? PlayerTitles.Get(t).Name : null, HeroClasses.Get(heroClass).Name, avatarUrl, frame,
            rating, wins, losses, rewardGold);
}
