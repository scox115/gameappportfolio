using Game.Core.Entities;
using Game.Core.Seasons;
using Game.Core.Services;

namespace Game.Core.Tests.Seasons;

public class SeasonTests
{
    private static readonly Season October = Season.StartingOn(new DateOnly(2026, 10, 1));

    [Fact]
    public void ASeason_IsTheCalendarMonth_InUtc()
    {
        var season = Season.At(new DateTime(2026, 10, 31, 23, 59, 59, DateTimeKind.Utc));

        Assert.Equal(October, season);
        Assert.Equal("October 2026", season.Name);
        Assert.Equal("2026-10", season.Key);
        Assert.Equal(new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc), season.StartsAt);
        Assert.Equal(new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc), season.EndsAt);
        Assert.Equal("December 2026", season.Next().Next().Name);
        Assert.Equal("September 2026", season.Previous().Name);
        Assert.Throws<ArgumentException>(() => Season.At(new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Local)));
    }

    [Theory]
    [InlineData("2026-10", true)]
    [InlineData("2026-1", false)]
    [InlineData("October", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Keys_ReadBack(string? key, bool valid)
    {
        Assert.Equal(valid, Season.TryParse(key, out var season));
        if (valid) Assert.Equal(key, season.Key);
    }

    [Theory]
    [InlineData(1400, 1200)]
    [InlineData(1000, 1000)]
    [InlineData(1001, 1000)]
    [InlineData(800, 900)]
    [InlineData(100, 550)]
    public void TheSoftReset_MovesRatingsHalfwayBackToTheStart(int rating, int reset) =>
        Assert.Equal(reset, SeasonRules.SoftReset(rating));

    [Theory]
    [InlineData(1, 1000)]
    [InlineData(2, 500)]
    [InlineData(3, 500)]
    [InlineData(4, 250)]
    [InlineData(10, 250)]
    [InlineData(11, 50)]
    [InlineData(500, 50)]
    public void Rewards_FollowTheFinish(int rank, int gold) => Assert.Equal(gold, SeasonRules.RewardFor(rank));

    [Fact]
    public void AHeroFromBeforeSeasons_BringsTheirRatingIntoTheirFirstSeason()
    {
        var hero = Duelist(wins: 2);
        var before = hero.Rating;

        Assert.Null(hero.EnterSeason(October));

        Assert.Equal(before, hero.Rating);
        Assert.Equal(October.Start, hero.SeasonStart);
        Assert.Equal((0, 0), (hero.SeasonWins, hero.SeasonLosses));
        Assert.Empty(hero.SeasonRecords);
    }

    [Fact]
    public void ANewSeason_KeepsTheOldRecord_AndResetsTheRating()
    {
        var hero = new Player("hero", 0);
        hero.EnterSeason(October);
        hero.RecordPvpWin(150);
        hero.RecordPvpWin(150);
        hero.RecordPvpLoss(20);

        var record = hero.EnterSeason(October.Next());

        Assert.NotNull(record);
        Assert.Equal((October.Start, 1280, 2, 1), (record.SeasonStart, record.Rating, record.Wins, record.Losses));
        Assert.False(record.Settled);
        Assert.Equal(1140, hero.Rating);
        Assert.Equal((0, 0), (hero.SeasonWins, hero.SeasonLosses));
        Assert.Equal(October.Next().Start, hero.SeasonStart);
        Assert.Equal((2, 1), (hero.PvpWins, hero.PvpLosses)); // the all-time record stays
    }

    [Fact]
    public void EnteringTheSameSeasonAgain_ChangesNothing()
    {
        var hero = new Player("hero", 0);
        hero.EnterSeason(October);
        hero.RecordPvpWin(100);

        Assert.Null(hero.EnterSeason(October));
        Assert.Null(hero.EnterSeason(October.Previous()));
        Assert.Equal((1100, 1), (hero.Rating, hero.SeasonWins));
    }

    [Fact]
    public void ASeasonWithoutDuels_LeavesNoRecord_ButStillResets()
    {
        var hero = new Player("hero", 0);
        hero.EnterSeason(October);
        hero.RecordPvpWin(200);
        hero.EnterSeason(October.Next());   // 1200 -> 1100, October kept

        Assert.Null(hero.EnterSeason(October.Next().Next()));
        Assert.Equal(1050, hero.Rating);
        Assert.Single(hero.SeasonRecords);
    }

    [Fact]
    public void SettlingASeason_PaysTheReward_Once()
    {
        var hero = new Player("hero", 0);
        hero.EnterSeason(October);
        hero.RecordPvpWin(10);
        hero.EnterSeason(October.Next());

        hero.SettleSeason(October, rank: 2);

        var record = Assert.Single(hero.SeasonRecords);
        Assert.Equal((2, 500, true), (record.Rank, record.RewardGold, record.Settled));
        Assert.Equal(500, hero.Gold);
        Assert.Throws<InvalidOperationException>(() => hero.SettleSeason(October, rank: 2));
        Assert.Throws<InvalidOperationException>(() => hero.SettleSeason(October.Next(), rank: 1));
    }

    [Fact]
    public void AnUnrankedFinish_PaysNothing()
    {
        var hero = new Player("hero", 0);
        hero.EnterSeason(October);
        hero.RecordPvpLoss(10);
        hero.EnterSeason(October.Next());

        hero.SettleSeason(October, rank: null);

        Assert.Equal((null, 0), (Assert.Single(hero.SeasonRecords).Rank, hero.Gold));
    }

    [Fact]
    public void TheFirstDuelOfANewSeason_ResetsBothHeroesBeforeTheRatingMoves()
    {
        var winner = new Player("winner", 0);
        var loser = new Player("loser", 0);
        winner.EnterSeason(October);
        winner.RecordPvpWin(400);   // 1400
        loser.EnterSeason(October);
        loser.RecordPvpLoss(200);   // 800

        var november = new FixedClock(new DateOnly(2026, 11, 2));
        var settlement = new MatchRulesEngine(november).ProcessMatchWin(new GameMatch(winner.Id, loser.Id), winner, loser);

        // 1400 -> 1200 and 800 -> 900 first, then the duel is rated between those.
        Assert.Equal(EloRating.PointsForWin(1200, 900), settlement.Winner.RatingChange);
        Assert.Equal(1200 + settlement.Winner.RatingChange, winner.Rating);
        Assert.Equal((1, 0), (winner.SeasonWins, winner.SeasonLosses));
        Assert.Equal((0, 1), (loser.SeasonWins, loser.SeasonLosses));
        Assert.Equal(1400, Assert.Single(winner.SeasonRecords).Rating);
        Assert.Equal(800, Assert.Single(loser.SeasonRecords).Rating);
    }

    private static Player Duelist(int wins)
    {
        var hero = new Player("hero", 0);
        for (var i = 0; i < wins; i++) hero.RecordPvpWin(16);
        return hero;
    }
}
