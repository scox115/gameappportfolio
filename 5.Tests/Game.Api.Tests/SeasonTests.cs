using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Game.Api.Models;
using Game.Api.Seasons;
using Game.Core.Entities;
using Game.Core.Seasons;
using Game.Core.Services;
using Game.Infrastructure.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Game.Api.Tests;

// Monthly ranked seasons: live standings, closing a season, rewards and the soft reset (docs/adr/0035-ranked-seasons.md).
public class SeasonTests
{
    private const string Password = "Arena-Pass1";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    [Fact]
    public async Task WhenASeasonEnds_HeroesAreRankedAndPaid_AndRatingsReset()
    {
        using var factory = new GameApiFactory();
        var season = Season.At(factory.Clock.GetUtcNow().UtcDateTime);
        var first = await SignUpAsync(factory);
        var second = await SignUpAsync(factory);
        var third = await SignUpAsync(factory);
        var tooFewDuels = await SignUpAsync(factory);
        var guest = await StartGuestAsync(factory);
        await DuelAsync(factory, first.Id, wins: 3, losses: 1, pointsEach: 100);   // 1300 - 100 = 1200
        await DuelAsync(factory, second.Id, wins: 3, losses: 0, pointsEach: 50);   // 1150
        await DuelAsync(factory, third.Id, wins: 1, losses: 2, pointsEach: 20);    // 980
        await DuelAsync(factory, tooFewDuels.Id, wins: 2, losses: 0, pointsEach: 500);
        await DuelAsync(factory, guest.Id, wins: 5, losses: 0, pointsEach: 500);

        // While the season is under way the standings are live, with what each place would pay.
        var live = await GetStandingsAsync(first.Client, season.Key);
        Assert.False(live.Final);
        Assert.Equal([first.Username, second.Username, third.Username], live.Standings.Select(s => s.Username));
        Assert.Equal([1200, 1150, 980], live.Standings.Select(s => s.Rating));
        Assert.Equal([1000, 500, 500], live.Standings.Select(s => s.RewardGold));
        var mine = await first.Client.GetFromJsonAsync<MySeasonResponse>("/api/v1/seasons/me", Json);
        Assert.Equal((1, 3, 1), (mine!.Rank, mine.Wins, mine.Losses));
        Assert.Null((await tooFewDuels.Client.GetFromJsonAsync<MySeasonResponse>("/api/v1/seasons/me", Json))!.Rank);

        // Nothing closes while the season is under way.
        Assert.Empty(await CloseSeasonsAsync(factory));

        factory.Clock.Advance(season.EndsAt - factory.Clock.GetUtcNow().UtcDateTime + TimeSpan.FromMinutes(1));
        Assert.Equal([season], await CloseSeasonsAsync(factory));

        var final = await GetStandingsAsync(first.Client, season.Key);
        Assert.True(final.Final);
        Assert.Equal([(1, first.Username, 1000), (2, second.Username, 500), (3, third.Username, 500)],
            final.Standings.Select(s => (s.Rank, s.Username, s.RewardGold)));
        Assert.Equal([1200, 1150, 980], final.Standings.Select(s => s.Rating));

        var firstNow = await first.Client.GetFromJsonAsync<MySeasonResponse>("/api/v1/seasons/me", Json);
        Assert.Equal(season.Next().Key, firstNow!.Season.Key);
        Assert.Equal((SeasonRules.SoftReset(1200), 0, 0, (int?)null), (firstNow.Rating, firstNow.Wins, firstNow.Losses, firstNow.Rank));
        Assert.Equal((season.Key, (int?)1, 1200, 1000), (firstNow.LastSeason!.Key, firstNow.LastSeason.Rank, firstNow.LastSeason.Rating, firstNow.LastSeason.RewardGold));
        Assert.Equal(Player.StartingGold + 1000, (await ProfileAsync(first.Client)).Gold);
        Assert.Equal(SeasonRules.SoftReset(1200), (await ProfileAsync(first.Client)).Rating);

        var unranked = (await tooFewDuels.Client.GetFromJsonAsync<MySeasonResponse>("/api/v1/seasons/me", Json))!.LastSeason;
        Assert.Equal(((int?)null, 0), (unranked!.Rank, unranked.RewardGold));
        Assert.Equal(Player.StartingGold, (await ProfileAsync(tooFewDuels.Client)).Gold);
        Assert.Equal(Player.StartingGold, (await ProfileAsync(guest.Client)).Gold);

        var seasons = await first.Client.GetFromJsonAsync<SeasonsResponse>("/api/v1/seasons", Json);
        Assert.Equal(season.Next().Key, seasons!.Current.Key);
        Assert.Equal((season.Key, season.Name, 3), (seasons.Past[0].Key, seasons.Past[0].Name, seasons.Past[0].RankedHeroes));

        // Closing again finds nothing to do and pays nothing twice.
        Assert.Empty(await CloseSeasonsAsync(factory));
        Assert.Equal(Player.StartingGold + 1000, (await ProfileAsync(first.Client)).Gold);
    }

    [Fact]
    public async Task HeroesFromBeforeSeasons_KeepTheirRatingThroughTheFirstSeason_ThenResetToo()
    {
        using var factory = new GameApiFactory();
        var veteran = await SignUpAsync(factory);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var player = (await db.Players.FindAsync(veteran.Id))!;
            player.RecordPvpWin(400); // before seasons, so not in any season yet
            await db.SaveChangesAsync();
        }

        // Moved to the last minute of the first season: still 1400.
        var now = factory.Clock.GetUtcNow().UtcDateTime;
        factory.Clock.Advance(SeasonRules.FirstSeason.EndsAt - now - TimeSpan.FromMinutes(1));
        Assert.Empty(await CloseSeasonsAsync(factory));
        Assert.Equal(1400, (await ProfileAsync(veteran.Client)).Rating);

        factory.Clock.Advance(TimeSpan.FromMinutes(2));
        await CloseSeasonsAsync(factory);
        Assert.Equal(1200, (await ProfileAsync(veteran.Client)).Rating);
        Assert.Null((await veteran.Client.GetFromJsonAsync<MySeasonResponse>("/api/v1/seasons/me", Json))!.LastSeason);
    }

    [Theory]
    [InlineData("2020-01", HttpStatusCode.NotFound)]
    [InlineData("2099-12", HttpStatusCode.NotFound)]
    [InlineData("october", HttpStatusCode.BadRequest)]
    public async Task OnlyTheCurrentAndClosedSeasons_HaveStandings(string key, HttpStatusCode expected)
    {
        using var factory = new GameApiFactory();
        var response = await factory.CreateClient().GetAsync($"/api/v1/seasons/{key}/standings");
        Assert.Equal(expected, response.StatusCode);
    }

    [Theory, MemberData(nameof(TestDatabase.Engines), MemberType = typeof(TestDatabase))]
    public async Task TwoReplicasClosingTheSameSeason_PayTheRewardsOnce(string engine)
    {
        using var database = TestDatabase.Create(engine);
        var october = SeasonRules.FirstSeason;
        Guid heroId;
        using (var db = database.NewContext())
        {
            var hero = new Player($"season{Guid.NewGuid():N}"[..16], startingGold: 0);
            hero.EnterSeason(october);
            for (var i = 0; i < SeasonRules.DuelsToBeRanked; i++) hero.RecordPvpWin(10);
            db.Players.Add(hero);
            await db.SaveChangesAsync();
            heroId = hero.Id;
        }

        var november = new TestClock();
        november.Advance(october.EndsAt.AddHours(1) - november.GetUtcNow().UtcDateTime);
        async Task<IReadOnlyList<Season>> CloseOnAReplicaAsync()
        {
            await using var db = database.NewContext();
            return await new SeasonService(db, november, NullLogger<SeasonService>.Instance).CloseFinishedSeasonsAsync();
        }

        var runs = await Task.WhenAll(CloseOnAReplicaAsync(), CloseOnAReplicaAsync());

        Assert.Equal([october], runs.SelectMany(closed => closed));
        using (var db = database.NewContext())
        {
            Assert.Equal(october.Start, Assert.Single(db.ClosedSeasons).SeasonStart);
            var hero = db.Players.Single(p => p.Id == heroId);
            Assert.Equal(SeasonRules.RewardFor(1), hero.Gold);
            Assert.Equal(1, Assert.Single(hero.SeasonRecords).Rank);
        }
    }

    private static async Task<IReadOnlyList<Season>> CloseSeasonsAsync(GameApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<SeasonService>().CloseFinishedSeasonsAsync();
    }

    // Records duels straight onto the hero, as a finished duel would, without playing them.
    private static async Task DuelAsync(GameApiFactory factory, Guid playerId, int wins, int losses, int pointsEach)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var player = (await db.Players.FindAsync(playerId))!;
        player.EnterSeason(Season.At(factory.Clock.GetUtcNow().UtcDateTime));
        for (var i = 0; i < wins; i++) player.RecordPvpWin(pointsEach);
        for (var i = 0; i < losses; i++) player.RecordPvpLoss(pointsEach);
        await db.SaveChangesAsync();
    }

    private static async Task<SeasonStandingsResponse> GetStandingsAsync(HttpClient client, string key) =>
        (await client.GetFromJsonAsync<SeasonStandingsResponse>($"/api/v1/seasons/{key}/standings", Json))!;

    private static async Task<PlayerProfileResponse> ProfileAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<PlayerProfileResponse>("/api/v1/players/me", Json))!;

    private static async Task<Hero> SignUpAsync(GameApiFactory factory)
    {
        var client = factory.CreateClient();
        var username = $"season{Guid.NewGuid():N}"[..16];
        var response = await client.PostAsJsonAsync("/api/v1/auth/register", new { Username = username, Password });
        response.EnsureSuccessStatusCode();
        return Authorize(client, (await response.Content.ReadFromJsonAsync<AuthResponse>(Json))!);
    }

    private static async Task<Hero> StartGuestAsync(GameApiFactory factory)
    {
        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/v1/auth/guest", new { });
        response.EnsureSuccessStatusCode();
        return Authorize(client, (await response.Content.ReadFromJsonAsync<AuthResponse>(Json))!);
    }

    private static Hero Authorize(HttpClient client, AuthResponse auth)
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        return new Hero(client, auth.Player.Id, auth.Player.Username);
    }

    private sealed record Hero(HttpClient Client, Guid Id, string Username);
}
