using Game.Core.Battles;
using Game.Core.Entities;
using Game.Core.Events;

namespace Game.Core.History;

/// <summary>
/// One line in a hero's match history, written from a <see cref="MatchCompletedEvent"/> by the
/// background consumer. It's a read model: rewards were already settled when the match ended.
/// </summary>
public class MatchHistoryEntry
{
    /// <summary>Shown instead of the name of an opponent who deleted their account.</summary>
    public const string RetiredHeroName = "A retired hero";

    public Guid Id { get; private set; }
    public Guid MatchId { get; private set; }
    public Guid PlayerId { get; private set; }
    public MatchKind Kind { get; private set; }
    public bool Won { get; private set; }
    public HeroClass Class { get; private set; }

    /// <summary>The other hero, or <see cref="GameMatch.AiBossId"/> for a boss fight.</summary>
    public Guid OpponentId { get; private set; }
    public string OpponentName { get; private set; } = string.Empty;
    public HeroClass? OpponentClass { get; private set; }

    public BossDifficulty? Difficulty { get; private set; }
    public PvpEndReason? EndReason { get; private set; }
    public int Turns { get; private set; }
    public int GoldEarned { get; private set; }
    public int ExperienceEarned { get; private set; }
    public int? RatingChange { get; private set; }
    public int WagerResult { get; private set; }
    public DateTime PlayedAt { get; private set; }

    private MatchHistoryEntry() { }

    /// <summary>The opponent deleted their account: keep the match, drop their name.</summary>
    public void RetireOpponent() => OpponentName = RetiredHeroName;

    /// <summary>Shows the opponent's new name after an admin renamed them, so an offensive name doesn't live on here.</summary>
    public void RenameOpponent(string name) => OpponentName = name;

    /// <summary>One entry per hero in the match; none for an event without participant details.</summary>
    public static IReadOnlyList<MatchHistoryEntry> From(MatchCompletedEvent match)
    {
        return match.Participants.Select(hero =>
        {
            var opponent = match.Participants.FirstOrDefault(p => p.PlayerId != hero.PlayerId);
            return new MatchHistoryEntry
            {
                Id = Guid.NewGuid(),
                MatchId = match.MatchId,
                PlayerId = hero.PlayerId,
                Kind = match.Kind,
                Won = hero.Won,
                Class = hero.Class,
                OpponentId = opponent?.PlayerId ?? GameMatch.AiBossId,
                OpponentName = opponent?.Username ?? BossProfile.For(match.Difficulty ?? BossDifficulty.Normal).Name,
                OpponentClass = opponent?.Class,
                Difficulty = match.Difficulty,
                EndReason = match.EndReason,
                Turns = match.Turns,
                GoldEarned = hero.Gold,
                ExperienceEarned = hero.Experience,
                RatingChange = hero.RatingChange,
                WagerResult = hero.WagerResult,
                PlayedAt = match.OccurredAt
            };
        }).ToList();
    }
}
