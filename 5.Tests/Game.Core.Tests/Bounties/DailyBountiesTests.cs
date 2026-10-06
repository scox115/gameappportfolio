using Game.Core.Bounties;
using Game.Core.Entities;

namespace Game.Core.Tests.Bounties;

public class DailyBountiesTests
{
    private static readonly DateOnly Today = new(2026, 10, 6);

    private static Player NewPlayer() => new("hunter", startingGold: 0);

    [Fact]
    public void EachDayHasThreeDifferentBounties_TheSameForEveryone()
    {
        var today = DailyBounties.For(Today);

        Assert.Equal(DailyBounties.PerDay, today.Count);
        Assert.Equal(today.Count, today.Select(b => b.Bounty).Distinct().Count());
        Assert.Equal(today, DailyBounties.For(Today));
    }

    [Fact]
    public void TheBountiesChangeFromDayToDay()
    {
        var week = Enumerable.Range(0, 7).Select(d => string.Join(",", DailyBounties.For(Today.AddDays(d)).Select(b => b.Bounty)));

        Assert.True(week.Distinct().Count() > 1);
    }

    [Fact]
    public void CompletingABounty_PaysItsRewardOnce()
    {
        var day = DayWith(Bounty.FightBattles);
        var bounty = DailyBounties.Get(Bounty.FightBattles);
        var player = NewPlayer();

        var paid = Enumerable.Range(0, bounty.Goal + 2)
            .Select(_ => player.RecordBattle(day, BattleKind.BossFight, won: false))
            .Count(b => b.CompletedBounties.Contains(bounty));

        Assert.Equal(1, paid);
        Assert.True(player.BountiesFor(day).Single(b => b.Definition == bounty).Completed);
        Assert.True(player.Gold >= bounty.Reward);
    }

    [Fact]
    public void OnlyTheRightKindOfBattleCounts()
    {
        var day = DayWith(Bounty.WinDuels);
        var player = NewPlayer();

        player.RecordBattle(day, BattleKind.BossFight, won: true);
        player.RecordBattle(day, BattleKind.Duel, won: false);

        Assert.Equal(0, player.BountiesFor(day).Single(b => b.Definition.Bounty == Bounty.WinDuels).Progress);

        player.RecordBattle(day, BattleKind.Duel, won: true);
        Assert.Equal(1, player.BountiesFor(day).Single(b => b.Definition.Bounty == Bounty.WinDuels).Progress);
    }

    [Fact]
    public void ProgressStartsOverEachDay()
    {
        var day = DayWith(Bounty.FightBattles);
        var player = NewPlayer();
        player.RecordBattle(day, BattleKind.Duel, won: false);

        player.RecordBattle(day.AddDays(1), BattleKind.Duel, won: false);

        Assert.All(player.Bounties, b => Assert.Equal(day.AddDays(1), b.Day));
        Assert.Equal(0, player.BountiesFor(day).Sum(b => b.Progress));
    }

    [Fact]
    public void WinningInARow_PaysAGrowingStreakBonus_UntilALoss()
    {
        var player = NewPlayer();

        var bonuses = Enumerable.Range(0, 8)
            .Select(_ => player.RecordBattle(FixedClock.QuietDay, BattleKind.Duel, won: true).StreakBonus)
            .ToList();

        Assert.Equal([0, 10, 20, 30, 40, 50, 50, 50], bonuses);
        Assert.Equal(8, player.WinStreak);

        player.RecordBattle(FixedClock.QuietDay, BattleKind.Duel, won: false);
        Assert.Equal(0, player.WinStreak);
        Assert.Equal(0, player.RecordBattle(FixedClock.QuietDay, BattleKind.Duel, won: true).StreakBonus);
    }

    private static DateOnly DayWith(Bounty bounty) => Enumerable.Range(0, 365)
        .Select(offset => Today.AddDays(offset))
        .First(d => DailyBounties.For(d).Any(b => b.Bounty == bounty));
}
