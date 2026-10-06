using Game.Core.Battles;

namespace Game.Core.Events;

public enum MatchKind
{
    Boss,
    Duel
}

/// <summary>One hero's side of a finished match: who they were and what the match paid them.</summary>
public record MatchParticipant(
    Guid PlayerId,
    string Username,
    HeroClass Class,
    bool Won,
    int Gold,
    int Experience,
    int? RatingChange = null,
    int WagerResult = 0);

/// <summary>
/// Published after a match's rewards are saved. Background consumers build match history and
/// daily stats from it; they never change balances, which the API settles on the request.
/// </summary>
/// <remarks>
/// Events queued before the details were added carry only the three ids; consumers skip them.
/// </remarks>
// Using a record ensures our event message is immutable
public record MatchCompletedEvent(
    Guid MatchId, 
    Guid WinnerId, 
    Guid LoserId
)
{
    public DateTime OccurredAt { get; init; }
    public MatchKind Kind { get; init; }

    /// <summary>Boss fights only.</summary>
    public BossDifficulty? Difficulty { get; init; }

    /// <summary>Duels only.</summary>
    public PvpEndReason? EndReason { get; init; }

    public int Turns { get; init; }

    /// <summary>The heroes who fought: one for a boss fight, two for a duel.</summary>
    public IReadOnlyList<MatchParticipant> Participants { get; init; } = [];
}
