using System.Security.Claims;
using Game.Api.Auth;
using Game.Api.Caching;
using Game.Api.Models;
using Game.Core.Events;
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

            // Duels whose moves were recorded can be replayed (duels from before replays existed can't).
            var matchIds = entries.Where(e => e.Kind == MatchKind.Duel).Select(e => (Guid?)e.MatchId).ToList();
            var replays = await dbContext.PvpBattles
                .Where(b => matchIds.Contains(b.MatchId) && dbContext.DuelMoves.Any(m => m.BattleId == b.Id))
                .Select(b => new { MatchId = b.MatchId!.Value, b.Id })
                .ToDictionaryAsync(b => b.MatchId, b => b.Id);

            return Results.Ok(entries.Select(e => MatchHistoryItemResponse.From(e, replays.TryGetValue(e.MatchId, out var id) ? id : null)));
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
