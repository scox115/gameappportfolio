using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Game.Api.Messaging;
using Game.Api.Models;
using Game.Core.Battles;
using Game.Core.Events;
using Game.Infrastructure.Data;
using Game.Infrastructure.History;
using Microsoft.Extensions.DependencyInjection;

namespace Game.Api.Tests;

// The RabbitMQ consumer isn't running in tests, so these take events off the in-memory backlog and
// hand them to the projector the consumer uses.
public class MatchHistoryTests
{
    private const string Password = "Arena-Pass1";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    [Fact]
    public async Task WinningABossFight_PublishesTheDetailsThatBecomeHistoryAndStats()
    {
        using var factory = new GameApiFactory();
        var (client, playerId, username) = await SignedInAsync(factory);

        var final = await WinBossFightAsync(client);

        var published = DrainPublished(factory).Single();
        Assert.Equal(MatchKind.Boss, published.Kind);
        Assert.Equal(BossDifficulty.Normal, published.Difficulty);
        Assert.Equal(playerId, published.WinnerId);
        var hero = Assert.Single(published.Participants);
        Assert.Equal((playerId, username, true), (hero.PlayerId, hero.Username, hero.Won));
        Assert.Equal(final.Reward!.GoldEarned, hero.Gold);
        Assert.Equal(final.Battle.Turn, published.Turns);

        Assert.Equal(ProjectionResult.Recorded, await ProjectAsync(factory, published));

        var history = await client.GetFromJsonAsync<List<MatchHistoryItemResponse>>("/api/players/me/matches", Json);
        var entry = Assert.Single(history!);
        Assert.True(entry.Won);
        Assert.Equal(MatchKind.Boss, entry.Kind);
        Assert.Equal(BossProfile.Normal.Name, entry.OpponentName);
        Assert.Equal(final.Reward.GoldEarned, entry.GoldEarned);
        Assert.Null(entry.RatingChange);

        var today = (await client.GetFromJsonAsync<List<DailyArenaStatsResponse>>("/api/arena/stats?days=1", Json))!.Single();
        Assert.Equal((1, 1, 0), (today.BossFights, today.BossWins, today.Duels));
        Assert.Equal(final.Reward.GoldEarned, today.GoldPaid);
    }

    [Fact]
    public async Task ADuel_GivesBothHeroesAnEntryFromTheirOwnSide()
    {
        using var factory = new GameApiFactory();
        var (winnerClient, winnerId, winnerName) = await SignedInAsync(factory);
        var (loserClient, loserId, loserName) = await SignedInAsync(factory);
        var duel = Duel(winnerId, winnerName, loserId, loserName);

        Assert.Equal(ProjectionResult.Recorded, await ProjectAsync(factory, duel));

        var won = (await winnerClient.GetFromJsonAsync<List<MatchHistoryItemResponse>>("/api/players/me/matches", Json))!.Single();
        var lost = (await loserClient.GetFromJsonAsync<List<MatchHistoryItemResponse>>("/api/players/me/matches", Json))!.Single();
        Assert.Equal((true, loserName, HeroClass.Ranger, 14), (won.Won, won.OpponentName, won.OpponentClass, won.RatingChange));
        Assert.Equal((false, winnerName, HeroClass.Paladin, -14), (lost.Won, lost.OpponentName, lost.OpponentClass, lost.RatingChange));
        Assert.Equal(50, won.WagerResult);
        Assert.Equal(PvpEndReason.Forfeit, lost.EndReason);
    }

    [Fact]
    public async Task ARepeatedDelivery_IsRecordedOnce()
    {
        using var factory = new GameApiFactory();
        var duel = Duel(Guid.NewGuid(), "alpha", Guid.NewGuid(), "bravo");

        Assert.Equal(ProjectionResult.Recorded, await ProjectAsync(factory, duel));
        Assert.Equal(ProjectionResult.Duplicate, await ProjectAsync(factory, duel));

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(2, db.MatchHistory.Count(e => e.MatchId == duel.MatchId));
        Assert.Equal(1, db.DailyArenaStats.Single().Duels);
    }

    [Fact]
    public async Task EventsQueuedBeforeDetailsWereAdded_AreSkipped()
    {
        using var factory = new GameApiFactory();
        // What the API published before this change: only the three ids, enum-free.
        var legacy = JsonSerializer.Deserialize<MatchCompletedEvent>(
            $$"""{"MatchId":"{{Guid.NewGuid()}}","WinnerId":"{{Guid.NewGuid()}}","LoserId":"{{Guid.NewGuid()}}"}""",
            MatchEventJson.Options)!;

        Assert.Equal(ProjectionResult.NoDetails, await ProjectAsync(factory, legacy));

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Empty(db.MatchHistory);
        Assert.Empty(db.DailyArenaStats);
    }

    [Fact]
    public void Events_SurviveTheTripThroughTheQueue()
    {
        var duel = Duel(Guid.NewGuid(), "alpha", Guid.NewGuid(), "bravo");

        var json = JsonSerializer.Serialize(duel, MatchEventJson.Options);
        var read = JsonSerializer.Deserialize<MatchCompletedEvent>(json, MatchEventJson.Options)!;

        Assert.Contains("\"kind\":\"Duel\"", json);
        Assert.Equal(duel.Participants, read.Participants);
        Assert.Equal(duel with { Participants = read.Participants }, read);
    }

    [Fact]
    public async Task ArenaStats_ListEveryDayNewestFirstWithZerosForQuietDays()
    {
        using var factory = new GameApiFactory();

        var days = await factory.CreateClient().GetFromJsonAsync<List<DailyArenaStatsResponse>>("/api/arena/stats?days=3", Json);

        var today = DateOnly.FromDateTime(factory.Clock.GetUtcNow().UtcDateTime);
        Assert.Equal([today, today.AddDays(-1), today.AddDays(-2)], days!.Select(d => d.Day));
        Assert.All(days!, d => Assert.Equal(0, d.BossFights + d.Duels));
    }

    [Fact]
    public async Task MatchHistory_NeedsASignedInHero()
    {
        using var factory = new GameApiFactory();

        var response = await factory.CreateClient().GetAsync("/api/players/me/matches");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static MatchCompletedEvent Duel(Guid winnerId, string winnerName, Guid loserId, string loserName) =>
        new(Guid.NewGuid(), winnerId, loserId)
        {
            OccurredAt = DateTime.UtcNow,
            Kind = MatchKind.Duel,
            EndReason = PvpEndReason.Forfeit,
            Turns = 6,
            Participants =
            [
                new MatchParticipant(winnerId, winnerName, HeroClass.Paladin, true, 100, 50, 14, 50),
                new MatchParticipant(loserId, loserName, HeroClass.Ranger, false, 20, 10, -14, -50)
            ]
        };

    private static List<MatchCompletedEvent> DrainPublished(GameApiFactory factory)
    {
        var publisher = factory.Services.GetRequiredService<MatchTelemetryPublisher>();
        var events = new List<MatchCompletedEvent>();
        while (publisher.Pending.TryRead(out var e)) events.Add(e);
        return events;
    }

    private static async Task<ProjectionResult> ProjectAsync(GameApiFactory factory, MatchCompletedEvent match)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<MatchHistoryProjector>().ProjectAsync(match);
    }

    private static async Task<(HttpClient Client, Guid PlayerId, string Username)> SignedInAsync(GameApiFactory factory)
    {
        var client = factory.CreateClient();
        var username = $"hist{Guid.NewGuid():N}"[..20];
        var response = await client.PostAsJsonAsync("/api/auth/register", new { Username = username, Password });
        response.EnsureSuccessStatusCode();
        var auth = (await response.Content.ReadFromJsonAsync<AuthResponse>(Json))!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        return (client, auth.Player.Id, username);
    }

    // FixedBattleRandom makes every card land, so alternating the two attacks always wins.
    private static async Task<PlayCardResponse> WinBossFightAsync(HttpClient client)
    {
        var start = await client.PostAsync("/api/battles/pve", null);
        start.EnsureSuccessStatusCode();
        var battle = (await start.Content.ReadFromJsonAsync<BattleStateResponse>(Json))!;

        BattleCard? recharging = null;
        for (var turn = 0; turn < 50; turn++)
        {
            var card = recharging == BattleCard.DragonClaw ? "Fireball" : "DragonClaw";
            var response = await client.PostAsJsonAsync($"/api/battles/pve/{battle.Id}/turns", new { Card = card });
            response.EnsureSuccessStatusCode();
            var result = (await response.Content.ReadFromJsonAsync<PlayCardResponse>(Json))!;
            if (result.Battle.Status != BattleStatus.InProgress)
            {
                Assert.Equal(BattleStatus.Won, result.Battle.Status);
                return result;
            }
            recharging = result.Battle.RechargingCard;
        }

        throw new InvalidOperationException("Battle did not finish within 50 turns.");
    }
}
