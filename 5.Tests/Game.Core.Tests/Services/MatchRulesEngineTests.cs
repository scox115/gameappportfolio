using Game.Core.Battles;
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

    [Fact]
    public void BossWins_PayInFullOnlyUpToTheDailyLimit()
    {
        var player = NewPlayer();

        var rewards = Enumerable.Range(0, Player.FullRewardBossWinsPerDay + 2)
            .Select(_ => _engine.ProcessPveMatch(GameMatch.CreatePve(player.Id), player, isVictory: true))
            .ToList();

        var full = rewards.Take(Player.FullRewardBossWinsPerDay).ToList();
        var reduced = rewards.Skip(Player.FullRewardBossWinsPerDay).ToList();
        Assert.All(full, r => Assert.False(r.ReducedBossReward));
        Assert.All(reduced, r =>
        {
            Assert.True(r.ReducedBossReward);
            Assert.Equal(0, r.Bonuses.StreakBonus);
            Assert.Equal(MatchRulesEngine.ReducedBossWinGold, r.Gold);
            Assert.Equal(MatchRulesEngine.WinExperience, r.Experience);
        });
        Assert.Equal(0, player.FullRewardBossWinsLeft(FixedClock.QuietDay));
    }

    [Fact]
    public void BossLosses_DontUseUpTheDailyLimit_AndTheLimitResetsTomorrow()
    {
        var player = NewPlayer();
        for (var i = 0; i < 10; i++) _engine.ProcessPveMatch(GameMatch.CreatePve(player.Id), player, isVictory: false);
        Assert.Equal(Player.FullRewardBossWinsPerDay, player.FullRewardBossWinsLeft(FixedClock.QuietDay));

        for (var i = 0; i < Player.FullRewardBossWinsPerDay; i++) _engine.ProcessPveMatch(GameMatch.CreatePve(player.Id), player, isVictory: true);
        Assert.Equal(0, player.FullRewardBossWinsLeft(FixedClock.QuietDay));

        var tomorrow = new MatchRulesEngine(new FixedClock(FixedClock.QuietDay.AddDays(1)));
        Assert.False(tomorrow.ProcessPveMatch(GameMatch.CreatePve(player.Id), player, isVictory: true).ReducedBossReward);
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
    public void Pvp_RecordsTheResultUnderTheClassEachHeroDueledAs()
    {
        var winner = NewPlayer("a");
        var loser = NewPlayer("b");

        // The winner switched class mid-duel; the duel still counts for the class they fought as.
        _engine.ProcessMatchWin(new GameMatch(winner.Id, loser.Id), winner, loser,
            winnerClass: HeroClass.Paladin, loserClass: HeroClass.Ranger);
        _engine.ProcessMatchWin(new GameMatch(winner.Id, loser.Id), winner, loser);

        Assert.Equal(2, winner.PvpWins);
        Assert.Equal(1, winner.ClassRecords.Single(r => r.Class == HeroClass.Paladin).Wins);
        Assert.Equal(1, winner.ClassRecords.Single(r => r.Class == winner.Class).Wins);
        Assert.All(winner.ClassRecords, r => Assert.Equal(0, r.Losses));
        Assert.Equal(1, loser.ClassRecords.Single(r => r.Class == HeroClass.Ranger).Losses);
        Assert.Equal(2, loser.ClassRecords.Sum(r => r.Losses));
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
