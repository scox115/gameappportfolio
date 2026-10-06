using System.Security.Claims;
using Game.Api.Auth;
using Game.Api.Caching;
using Game.Api.Models;
using Game.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Game.Api.Endpoints;

// Match history and arena stats. Both are built in the background from RabbitMQ match events
// (see MatchConsumerWorker), so a match shows up a moment after it ends.
public static class HistoryEndpoints
{
    public const int MaxMatches = 50;
    public const int MaxDays = 30;

    public static void MapHistoryEndpoints(this IEndpointRouteBuilder app)
    {
        // GET: /api/players/me/matches?limit=10, newest first
        app.MapGet("/players/me/matches", async (ClaimsPrincipal user, AppDbContext dbContext, int? limit) =>
        {
            var playerId = user.GetPlayerId();
            var take = Math.Clamp(limit ?? 10, 1, MaxMatches);

            var entries = await dbContext.MatchHistory
                .Where(e => e.PlayerId == playerId)
                .OrderByDescending(e => e.PlayedAt)
                .Take(take)
                .ToListAsync();

            return Results.Ok(entries.Select(MatchHistoryItemResponse.From));
        })
        .WithTags("Players")
        .RequireAuthorization();

        // GET: /api/arena/stats?days=7, one row per UTC day, newest first (days with no matches are zeros)
        app.MapGet("/arena/stats", async (AppDbContext dbContext, TimeProvider timeProvider, int? days) =>
        {
            var count = Math.Clamp(days ?? 7, 1, MaxDays);
            var today = DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);
            var first = today.AddDays(1 - count);

            var stored = await dbContext.DailyArenaStats
                .Where(s => s.Day >= first && s.Day <= today)
                .ToDictionaryAsync(s => s.Day);

            return Results.Ok(Enumerable.Range(0, count)
                .Select(offset => today.AddDays(-offset))
                .Select(day => stored.TryGetValue(day, out var s) ? DailyArenaStatsResponse.From(s) : DailyArenaStatsResponse.Empty(day)));
        })
        .WithTags("Arena")
        .CacheOutput(OutputCaching.Policies.ArenaStats);
    }
}
