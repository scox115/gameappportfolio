using Game.Core.Battles;
using Game.Core.Entities;
using Game.Core.Services;

namespace Game.Core.Tests.Battles;

public class DuelWagersTests
{
    private readonly MatchRulesEngine _engine = new(new FixedClock(FixedClock.QuietDay));

    [Theory]
    [InlineData(50, 10, 90)]
    [InlineData(100, 20, 180)]
    [InlineData(500, 100, 900)]
    public void TheWinnerTakesThePotMinusTheArenaFee(int stake, int fee, int payout)
    {
        Assert.Equal(fee, DuelWagers.Fee(stake));
        Assert.Equal(payout, DuelWagers.Payout(stake));
    }

    [Fact]
    public void AWageredDuel_PaysTheWinnerThePot()
    {
        var winner = new Player("a", 500);
        var loser = new Player("b", 500);
        winner.StakeWager(100);
        loser.StakeWager(100);

        var settlement = _engine.ProcessMatchWin(new GameMatch(winner.Id, loser.Id), winner, loser, wager: 100);

        Assert.Equal(400 + MatchRulesEngine.WinGold + 180, winner.Gold);
        Assert.Equal(400 + MatchRulesEngine.LossGold, loser.Gold);
        Assert.Equal(180, settlement.Winner.WagerResult);
        Assert.Equal(-100, settlement.Loser.WagerResult);
        Assert.Equal(MatchRulesEngine.WinGold + 180, settlement.Winner.Gold);
    }

    [Fact]
    public void AStakeNeedsTheGoldAndAnAllowedAmount()
    {
        var player = new Player("broke", 40);

        Assert.Throws<InvalidOperationException>(() => player.StakeWager(50));
        Assert.Throws<ArgumentOutOfRangeException>(() => player.StakeWager(75));
        player.StakeWager(0);
        Assert.Equal(40, player.Gold);
    }

    [Fact]
    public void ABattleRejectsAnOddWager()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PvpBattle.Start(Guid.NewGuid(), Guid.NewGuid(), DateTime.UtcNow, wager: 75));
        Assert.Equal(250, PvpBattle.Start(Guid.NewGuid(), Guid.NewGuid(), DateTime.UtcNow, wager: 250).Wager);
    }
}
