using Game.Core.Entities;
using Game.Core.Services;

namespace Game.Core.Tests.Services;

public class MatchRulesEngineTests
{
    private const int StartingGold = 500;
    // A day whose bounties all need more than one battle, so a single battle pays only its base reward.
    private readonly MatchRulesEngine _engine = new(new FixedClock(FixedClock.QuietDay));

    private static Player NewPlayer(string name = "hero") => new(name, StartingGold);

    // --- PvE ---

    [Fact]
    public void PveVictory_PaysWinRewardOnce()
    {
        var player = NewPlayer();
        var match = GameMatch.CreatePve(player.Id);

        _engine.ProcessPveMatch(match, player, isVictory: true);

        Assert.Equal(StartingGold + MatchRulesEngine.WinGold, player.Gold);
        Assert.Equal(MatchRulesEngine.WinExperience, player.ExperiencePoints);
    }

    [Fact]
    public void PveDefeat_PaysConsolationRewardOnly()
    {
        var player = NewPlayer();
        var match = GameMatch.CreatePve(player.Id);

        _engine.ProcessPveMatch(match, player, isVictory: false);

        Assert.Equal(StartingGold + MatchRulesEngine.LossGold, player.Gold);
        Assert.Equal(MatchRulesEngine.LossExperience, player.ExperiencePoints);
    }

    [Fact]
    public void PveVictory_PaysMoreThanDefeat()
    {
        var winner = NewPlayer("a");
        var loser = NewPlayer("b");

        _engine.ProcessPveMatch(GameMatch.CreatePve(winner.Id), winner, isVictory: true);
        _engine.ProcessPveMatch(GameMatch.CreatePve(loser.Id), loser, isVictory: false);

        Assert.True(winner.Gold > loser.Gold);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Pve_CompletesMatchWithCorrectWinner(bool isVictory)
    {
        var player = NewPlayer();
        var match = GameMatch.CreatePve(player.Id);

        _engine.ProcessPveMatch(match, player, isVictory);

        Assert.True(match.IsCompleted);
        Assert.Equal(isVictory ? player.Id : GameMatch.AiBossId, match.WinnerPlayerId);
    }

    [Fact]
    public void Pve_SettlingTwice_ThrowsAndDoesNotPayAgain()
    {
        var player = NewPlayer();
        var match = GameMatch.CreatePve(player.Id);
        _engine.ProcessPveMatch(match, player, isVictory: true);

        Assert.Throws<InvalidOperationException>(() => _engine.ProcessPveMatch(match, player, isVictory: true));
        Assert.Equal(StartingGold + MatchRulesEngine.WinGold, player.Gold);
    }

    [Fact]
    public void Pve_RejectsPlayerWhoIsNotInTheMatch()
    {
        var player = NewPlayer("a");
        var stranger = NewPlayer("b");
        var match = GameMatch.CreatePve(player.Id);

        Assert.Throws<ArgumentException>(() => _engine.ProcessPveMatch(match, stranger, isVictory: true));
        Assert.Equal(StartingGold, stranger.Gold);
        Assert.False(match.IsCompleted);
    }

    [Fact]
    public void Pve_RejectsPvpMatch()
    {
        var a = NewPlayer("a");
        var b = NewPlayer("b");
        var match = new GameMatch(a.Id, b.Id);

        Assert.Throws<ArgumentException>(() => _engine.ProcessPveMatch(match, a, isVictory: true));
    }

    // --- PvP ---

    [Fact]
    public void Pvp_PaysWinnerAndLoserOnceEach()
    {
        var winner = NewPlayer("a");
        var loser = NewPlayer("b");
        var match = new GameMatch(winner.Id, loser.Id);

        _engine.ProcessMatchWin(match, winner, loser);

        Assert.Equal(StartingGold + MatchRulesEngine.WinGold, winner.Gold);
        Assert.Equal(MatchRulesEngine.WinExperience, winner.ExperiencePoints);
        Assert.Equal(StartingGold + MatchRulesEngine.LossGold, loser.Gold);
        Assert.Equal(MatchRulesEngine.LossExperience, loser.ExperiencePoints);
        Assert.True(match.IsCompleted);
        Assert.Equal(winner.Id, match.WinnerPlayerId);
        Assert.Equal(1, winner.PvpWins);
        Assert.Equal(0, loser.PvpWins);
        Assert.Equal(0, winner.PvpLosses);
        Assert.Equal(1, loser.PvpLosses);
    }

    [Fact]
    public void Pvp_MovesRatingPointsFromTheLoserToTheWinner()
    {
        var winner = NewPlayer("a");
        var loser = NewPlayer("b");
        var match = new GameMatch(winner.Id, loser.Id);

        var points = _engine.ProcessMatchWin(match, winner, loser).Winner.RatingChange;

        Assert.Equal(EloRating.KFactor / 2, points);
        Assert.Equal(EloRating.StartingRating + points, winner.Rating);
        Assert.Equal(EloRating.StartingRating - points, loser.Rating);
    }

    [Fact]
    public void Pvp_RejectsSamePlayerAsWinnerAndLoser()
    {
        var player = NewPlayer();
        var match = GameMatch.CreatePve(player.Id);

        Assert.Throws<ArgumentException>(() => _engine.ProcessMatchWin(match, player, player));
        Assert.Equal(StartingGold, player.Gold);
    }

    [Fact]
    public void Pvp_RejectsPlayersOutsideTheMatch()
    {
        var a = NewPlayer("a");
        var b = NewPlayer("b");
        var c = NewPlayer("c");
        var match = new GameMatch(a.Id, b.Id);

        Assert.Throws<ArgumentException>(() => _engine.ProcessMatchWin(match, a, c));
        Assert.Equal(StartingGold, a.Gold);
        Assert.Equal(StartingGold, c.Gold);
    }
}
