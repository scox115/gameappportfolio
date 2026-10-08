using Game.Core.Battles;
using Game.Core.Entities;
using Game.Core.Seasons;
using Game.Core.Services;
using Game.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Game.Api.Seasons;

/// <summary>
/// Ends ranked seasons: keeps each hero's final rating and record, ranks them, pays the rewards and
/// softly resets ratings for the new season (see docs/adr/0035-ranked-seasons.md).
/// </summary>
public class SeasonService(AppDbContext db, TimeProvider timeProvider, ILogger<SeasonService> logger)
{
    public Season Current => Season.At(timeProvider.GetUtcNow().UtcDateTime);

    /// <summary>
    /// Closes every season that has ended and isn't closed yet, all in one save, so it either all
    /// happens or none of it does. A duel saved at the same moment, or another replica doing the
    /// same work, makes the save fail; the next run tries again.
    /// </summary>
    /// <returns>The seasons closed by this run.</returns>
    public async Task<IReadOnlyList<Season>> CloseFinishedSeasonsAsync(CancellationToken cancellationToken = default)
    {
        var current = Current;
        var startOfCurrent = current.Start;
        // Heroes who last dueled before seasons began keep that rating through the first season, then reset too.
        var resetPreSeasonHeroes = current.Start > SeasonRules.FirstSeason.Start;

        var players = await db.Players
            .Where(p => p.SeasonStart < startOfCurrent
                || (resetPreSeasonHeroes && p.SeasonStart == null && p.Rating != EloRating.StartingRating)
                || p.SeasonRecords.Any(r => !r.Settled))
            .ToListAsync(cancellationToken);
        if (players.Count == 0) return [];

        foreach (var player in players)
        {
            if (player.SeasonStart is null) player.EnterSeason(current.Previous());
            player.EnterSeason(current);
        }

        var closedAt = timeProvider.GetUtcNow().UtcDateTime;
        var alreadyClosed = await db.ClosedSeasons.Select(s => s.SeasonStart).ToListAsync(cancellationToken);
        var closed = new List<Season>();
        var bySeason = players
            .SelectMany(p => p.SeasonRecords.Where(r => !r.Settled).Select(r => (Player: p, Record: r)))
            .GroupBy(x => x.Record.Season)
            .OrderBy(g => g.Key.Start);
        foreach (var season in bySeason)
        {
            if (alreadyClosed.Contains(season.Key.Start))
            {
                // A record that turned up after its season closed is kept, but the standings are final.
                foreach (var (player, _) in season) player.SettleSeason(season.Key, rank: null);
                continue;
            }

            var ranked = Standings(season).ToList();
            foreach (var (player, _) in season)
            {
                var place = ranked.IndexOf(player);
                player.SettleSeason(season.Key, place < 0 ? null : place + 1);
            }
            db.ClosedSeasons.Add(new ClosedSeason(season.Key, closedAt, ranked.Count));
            closed.Add(season.Key);
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex)
        {
            logger.LogInformation(ex, "Closing the season clashed with another save; the next run tries again.");
            return [];
        }

        foreach (var season in closed)
        {
            logger.LogInformation("Closed the {Season} season with {Ranked} heroes ranked.",
                season.Name, players.Count(p => p.SeasonRecords.Any(r => r.SeasonStart == season.Start && r.Rank is not null)));
        }
        return closed;
    }

    // Highest rating first; more wins, then fewer losses, then the name, settle ties.
    private static IEnumerable<Player> Standings(IEnumerable<(Player Player, SeasonRecord Record)> records) =>
        records
            .Where(x => Ranks(x.Player) && x.Record.Duels >= SeasonRules.DuelsToBeRanked)
            .OrderByDescending(x => x.Record.Rating)
            .ThenByDescending(x => x.Record.Wins)
            .ThenBy(x => x.Record.Losses)
            .ThenBy(x => x.Player.Username, StringComparer.Ordinal)
            .Select(x => x.Player);

    /// <summary>Guests try the game and the Arena Bot only spars; standings are for heroes people kept.</summary>
    public static bool Ranks(Player player) => !player.IsGuest && player.Id != ArenaBot.Id;
}
