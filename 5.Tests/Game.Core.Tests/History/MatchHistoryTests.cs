using Game.Core.Battles;
using Game.Core.Entities;
using Game.Core.Events;
using Game.Core.History;

namespace Game.Core.Tests.History;

public class MatchHistoryTests
{
    private static readonly Guid Hero = Guid.NewGuid();
    private static readonly Guid Rival = Guid.NewGuid();

    [Fact]
    public void BossFight_GivesTheHeroOneEntryAgainstTheBoss()
    {
        var entry = Assert.Single(MatchHistoryEntry.From(Boss(BossDifficulty.Heroic, won: false)));

        Assert.Equal(Hero, entry.PlayerId);
        Assert.Equal(GameMatch.AiBossId, entry.OpponentId);
        Assert.Equal(BossProfile.Heroic.Name, entry.OpponentName);
        Assert.Null(entry.OpponentClass);
        Assert.False(entry.Won);
    }

    [Fact]
    public void Duel_GivesEachHeroTheOtherAsOpponent()
    {
        var entries = MatchHistoryEntry.From(Duel(PvpEndReason.Knockout));

        var winner = entries.Single(e => e.PlayerId == Hero);
        var loser = entries.Single(e => e.PlayerId == Rival);
        Assert.Equal((Rival, "rival", HeroClass.Ranger, true, 12), (winner.OpponentId, winner.OpponentName, winner.OpponentClass, winner.Won, winner.RatingChange));
        Assert.Equal((Hero, "hero", HeroClass.Sorcerer, false, -12), (loser.OpponentId, loser.OpponentName, loser.OpponentClass, loser.Won, loser.RatingChange));
    }

    [Fact]
    public void EventWithoutDetails_HasNoEntries() =>
        Assert.Empty(MatchHistoryEntry.From(new MatchCompletedEvent(Guid.NewGuid(), Hero, Rival)));

    [Fact]
    public void DailyStats_CountEachKindOfMatch()
    {
        var stats = new DailyArenaStats(new DateOnly(2026, 10, 6));

        stats.Record(Boss(BossDifficulty.Normal, won: true));
        stats.Record(Boss(BossDifficulty.Normal, won: false));
        stats.Record(Boss(BossDifficulty.Heroic, won: true));
        stats.Record(Duel(PvpEndReason.Knockout));
        stats.Record(Duel(PvpEndReason.Timeout));
        stats.Record(Duel(PvpEndReason.Forfeit));

        Assert.Equal((2, 1), (stats.BossFights, stats.BossWins));
        Assert.Equal((1, 1), (stats.HeroicFights, stats.HeroicWins));
        Assert.Equal((3, 1, 1, 1), (stats.Duels, stats.DuelKnockouts, stats.DuelTimeouts, stats.DuelForfeits));
        Assert.Equal(3 * 100 + 3 * (100 + 20), stats.GoldPaid);
    }

    [Fact]
    public void DailyStats_ChangeVersionOnEveryUpdate()
    {
        var stats = new DailyArenaStats(new DateOnly(2026, 10, 6));
        var before = stats.Version;

        stats.Record(Duel(PvpEndReason.Knockout));

        Assert.NotEqual(before, stats.Version);
    }

    private static MatchCompletedEvent Boss(BossDifficulty difficulty, bool won) =>
        new(Guid.NewGuid(), won ? Hero : GameMatch.AiBossId, won ? GameMatch.AiBossId : Hero)
        {
            Kind = MatchKind.Boss,
            Difficulty = difficulty,
            Participants = [new MatchParticipant(Hero, "hero", HeroClass.Sorcerer, won, 100, 50)]
        };

    private static MatchCompletedEvent Duel(PvpEndReason reason) =>
        new(Guid.NewGuid(), Hero, Rival)
        {
            Kind = MatchKind.Duel,
            EndReason = reason,
            Participants =
            [
                new MatchParticipant(Hero, "hero", HeroClass.Sorcerer, true, 100, 50, 12),
                new MatchParticipant(Rival, "rival", HeroClass.Ranger, false, 20, 10, -12)
            ]
        };
}
