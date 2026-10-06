using Game.Core.Entities;
using Game.Core.Services;

namespace Game.Core.Tests.Services;

public class EloRatingTests
{
    [Fact]
    public void EvenlyMatchedPlayers_HaveAnEvenChance()
    {
        Assert.Equal(0.5, EloRating.ExpectedScore(1200, 1200), precision: 6);
    }

    [Fact]
    public void A400PointGap_MakesTheFavouriteTenTimesAsLikelyToWin()
    {
        var favourite = EloRating.ExpectedScore(1400, 1000);

        Assert.Equal(10.0 / 11.0, favourite, precision: 6);
        Assert.Equal(1.0, favourite + EloRating.ExpectedScore(1000, 1400), precision: 6);
    }

    [Fact]
    public void EvenlyMatchedWin_IsWorthHalfTheKFactor()
    {
        Assert.Equal(EloRating.KFactor / 2, EloRating.PointsForWin(1000, 1000));
    }

    [Fact]
    public void BeatingAStrongerPlayer_EarnsMoreThanBeatingAWeakerOne()
    {
        var upset = EloRating.PointsForWin(1000, 1400);
        var expected = EloRating.PointsForWin(1400, 1000);

        Assert.Equal(29, upset);
        Assert.Equal(3, expected);
        Assert.Equal(EloRating.KFactor, upset + expected);
    }

    [Fact]
    public void AWinAlwaysEarnsAtLeastOnePoint()
    {
        Assert.Equal(1, EloRating.PointsForWin(3000, 100));
    }

    [Fact]
    public void RatingNeverDropsBelowTheMinimum()
    {
        var player = new Player("rookie", startingGold: 0);

        for (var i = 0; i < 100; i++) player.RecordPvpLoss(ratingLost: 32);

        Assert.Equal(EloRating.MinimumRating, player.Rating);
        Assert.Equal(100, player.PvpLosses);
    }

    [Fact]
    public void NewPlayers_StartAtTheBaseRating()
    {
        var player = new Player("rookie", startingGold: 0);

        Assert.Equal(EloRating.StartingRating, player.Rating);
        Assert.Equal(0, player.PvpWins);
        Assert.Equal(0, player.PvpLosses);
    }
}
