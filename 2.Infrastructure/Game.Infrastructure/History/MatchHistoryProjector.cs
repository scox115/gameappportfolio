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
    NoDetails
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
