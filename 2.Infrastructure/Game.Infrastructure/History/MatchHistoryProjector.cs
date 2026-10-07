using Game.Core.Events;
using Game.Core.History;
using Game.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Game.Infrastructure.History;

public enum ProjectionResult
{
    /// <summary>The match was added to history and the day's stats.</summary>
    Recorded,

    /// <summary>This match was already recorded; RabbitMQ delivered it again.</summary>
    Duplicate,

    /// <summary>An older event without match details; there's nothing to record.</summary>
    NoDetails,

    /// <summary>Every hero in the match has since deleted their account; nothing is kept.</summary>
    HeroesDeleted
}

/// <summary>
/// Turns a finished match into history entries and daily totals. Both are written in one
/// SaveChanges, so a match is either fully counted or not at all, and a repeat delivery is a no-op.
/// </summary>
public class MatchHistoryProjector(AppDbContext dbContext)
{
    public async Task<ProjectionResult> ProjectAsync(MatchCompletedEvent match, CancellationToken cancellationToken = default)
    {
        var entries = MatchHistoryEntry.From(match);
        if (entries.Count == 0)
        {
            return ProjectionResult.NoDetails;
        }

        // A hero may have deleted their account after the match ended and before this event
        // arrived: they get no entry, and their opponent's entry doesn't keep their name.
        var heroIds = entries.Select(e => e.PlayerId).ToList();
        var remaining = await dbContext.Players.Where(p => heroIds.Contains(p.Id)).Select(p => p.Id).ToListAsync(cancellationToken);
        foreach (var entry in entries.Where(e => heroIds.Contains(e.OpponentId) && !remaining.Contains(e.OpponentId)))
        {
            entry.RetireOpponent();
        }
        entries = entries.Where(e => remaining.Contains(e.PlayerId)).ToList();
        if (entries.Count == 0)
        {
            // Nothing is left to record it against, and without an entry a repeat delivery couldn't be
            // recognised, so the day's stats skip it too. It's one match nobody can see any more.
            return ProjectionResult.HeroesDeleted;
        }

        if (await dbContext.MatchHistory.AnyAsync(e => e.MatchId == match.MatchId, cancellationToken))
        {
            return ProjectionResult.Duplicate;
        }

        var day = DateOnly.FromDateTime(match.OccurredAt);
        var stats = await dbContext.DailyArenaStats.FindAsync([day], cancellationToken);
        if (stats is null)
        {
            stats = new DailyArenaStats(day);
            dbContext.DailyArenaStats.Add(stats);
        }

        stats.Record(match);
        dbContext.MatchHistory.AddRange(entries);
        await dbContext.SaveChangesAsync(cancellationToken);
        return ProjectionResult.Recorded;
    }
}
