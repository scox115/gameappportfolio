using Game.Core.Battles;

namespace Game.Core.Tests.Battles;

public class DuelRewardRulesTests
{
    private static readonly DateTime Now = new(2026, 10, 6, 12, 0, 0, DateTimeKind.Utc);
    private static readonly IBattleRandom Lucky = new MinimumRoll();

    [Fact]
    public void AFairDuel_Counts()
    {
        var battle = DuelAfterMoves(2 * DuelRewardRules.MinMovesEach);

        Assert.Null(DuelRewardRules.NoRewardReason(battle, rewardedDuelsToday: 0));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public void AForfeitBeforeBothPlayersMadeTheirMoves_DoesntCount(int moves)
    {
        var battle = DuelAfterMoves(moves);

        Assert.True(battle.EndedTooEarly);
        Assert.Contains("ended before", DuelRewardRules.NoRewardReason(battle, 0));
    }

    [Fact]
    public void APracticeDuel_NeverCounts()
    {
        var battle = PvpBattle.Start(Guid.NewGuid(), Guid.NewGuid(), Now, practice: true);
        PlayAndForfeit(battle, 2 * DuelRewardRules.MinMovesEach);

        Assert.Contains("Practice duel", DuelRewardRules.NoRewardReason(battle, 0));
    }

    [Fact]
    public void APracticeDuel_CantHaveAWager()
    {
        Assert.Throws<ArgumentException>(() => PvpBattle.Start(Guid.NewGuid(), Guid.NewGuid(), Now, wager: 50, practice: true));
    }

    [Fact]
    public void TooManyDuelsAgainstTheSameOpponent_DontCount()
    {
        var battle = DuelAfterMoves(2 * DuelRewardRules.MinMovesEach);

        Assert.Null(DuelRewardRules.NoRewardReason(battle, DuelRewardRules.RewardedDuelsPerOpponentPerDay - 1));
        Assert.Contains("against this opponent today", DuelRewardRules.NoRewardReason(battle, DuelRewardRules.RewardedDuelsPerOpponentPerDay));
    }

    private static PvpBattle DuelAfterMoves(int moves)
    {
        var battle = PvpBattle.Start(Guid.NewGuid(), Guid.NewGuid(), Now);
        PlayAndForfeit(battle, moves);
        return battle;
    }

    private static void PlayAndForfeit(PvpBattle battle, int moves)
    {
        for (var i = 0; i < moves; i++) battle.PlayCard(battle.ActivePlayerId, BattleCard.Fireball, Lucky, Now);
        Assert.Equal(moves, battle.MovesPlayed);
        battle.Forfeit(battle.ActivePlayerId, Now);
    }

    private sealed class MinimumRoll : IBattleRandom
    {
        public int Next(int minInclusive, int maxExclusive) => minInclusive;
    }
}
