using Game.Core.Battles;
using Game.Core.Events;
using Game.Core.History;

namespace Game.Api.Models;

public record MatchHistoryItemResponse(
    Guid MatchId,
    MatchKind Kind,
    bool Won,
    HeroClass Class,
    string OpponentName,
    HeroClass? OpponentClass,
    BossDifficulty? Difficulty,
    PvpEndReason? EndReason,
    int Turns,
    int GoldEarned,
    int ExperienceEarned,
    int? RatingChange,
    int WagerResult,
    DateTime PlayedAt,
    Guid? ReplayId = null)
{
    /// <param name="replayId">The duel to replay at /api/v1/duels/{id}/replay, when its moves were recorded.</param>
    public static MatchHistoryItemResponse From(MatchHistoryEntry e, Guid? replayId = null) =>
        new(e.MatchId, e.Kind, e.Won, e.Class, e.OpponentName, e.OpponentClass, e.Difficulty, e.EndReason,
            e.Turns, e.GoldEarned, e.ExperienceEarned, e.RatingChange, e.WagerResult, e.PlayedAt, replayId);
}

public record DailyArenaStatsResponse(
    DateOnly Day,
    int BossFights,
    int BossWins,
    int HeroicFights,
    int HeroicWins,
    int Duels,
    int DuelKnockouts,
    int DuelTimeouts,
    int DuelForfeits,
    int GoldPaid)
{
    public static DailyArenaStatsResponse From(DailyArenaStats s) =>
        new(s.Day, s.BossFights, s.BossWins, s.HeroicFights, s.HeroicWins, s.Duels,
            s.DuelKnockouts, s.DuelTimeouts, s.DuelForfeits, s.GoldPaid);

    public static DailyArenaStatsResponse Empty(DateOnly day) => new(day, 0, 0, 0, 0, 0, 0, 0, 0, 0);
}
