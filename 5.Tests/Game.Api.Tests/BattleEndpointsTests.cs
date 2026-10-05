using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Game.Api.Models;
using Game.Core.Battles;
using Game.Core.Services;

namespace Game.Api.Tests;

// Battles run on the server with FixedBattleRandom, so every boss attack rolls its minimum.
public class BattleEndpointsTests : IClassFixture<GameApiFactory>
{
    private const string Password = "Arena-Pass1";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly GameApiFactory _factory;

    public BattleEndpointsTests(GameApiFactory factory) => _factory = factory;

    [Fact]
    public async Task StartBattle_CreatesABattleAtFullHealth()
    {
        var client = await SignedInClientAsync();

        var response = await client.PostAsync("/api/battles/pve", null);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var battle = await response.Content.ReadFromJsonAsync<BattleStateResponse>(Json);
        Assert.Equal(PveBattle.PlayerMaxHp, battle!.PlayerHp);
        Assert.Equal(PveBattle.BossMaxHp, battle.BossHp);
        Assert.Equal(BattleStatus.InProgress, battle.Status);
    }

    [Fact]
    public async Task StartBattle_ResumesTheUnfinishedBattleInsteadOfStartingOver()
    {
        var client = await SignedInClientAsync();
        var first = await StartBattleAsync(client);
        await PlayAsync(client, first.Id, "Fireball");

        var response = await client.PostAsync("/api/battles/pve", null);
        var resumed = await response.Content.ReadFromJsonAsync<BattleStateResponse>(Json);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(first.Id, resumed!.Id);
        Assert.Equal(100, resumed.BossHp);
    }

    [Fact]
    public async Task PlayCard_ServerAppliesTheCardAndTheBossAttack()
    {
        var client = await SignedInClientAsync();
        var battle = await StartBattleAsync(client);

        var result = await PlayAsync(client, battle.Id, "Fireball");

        Assert.Equal(20, result.TurnResult.DamageDealt);
        Assert.Equal(PveBattle.OpeningBossAttack, result.TurnResult.BossDamage);
        Assert.Equal(100, result.Battle.BossHp);
        Assert.Equal(85, result.Battle.PlayerHp);
        Assert.Null(result.Reward);
    }

    [Fact]
    public async Task WinningABattle_PaysTheWinRewardOnceAndClosesTheBattle()
    {
        var client = await SignedInClientAsync();
        var battle = await StartBattleAsync(client);

        var final = await PlayUntilFinishedAsync(client, battle.Id, "DragonClaw");

        Assert.Equal(BattleStatus.Won, final.Battle.Status);
        Assert.Equal(MatchRulesEngine.WinGold, final.Reward!.GoldEarned);
        var me = await client.GetFromJsonAsync<PlayerProfileResponse>("/api/players/me", Json);
        Assert.Equal(500 + MatchRulesEngine.WinGold, me!.Gold);

        var extraMove = await client.PostAsJsonAsync($"/api/battles/pve/{battle.Id}/turns", new { Card = "Fireball" });
        Assert.Equal(HttpStatusCode.Conflict, extraMove.StatusCode);

        var next = await client.PostAsync("/api/battles/pve", null);
        Assert.Equal(HttpStatusCode.Created, next.StatusCode);
    }

    [Fact]
    public async Task LosingABattle_PaysOnlyTheConsolationReward()
    {
        var client = await SignedInClientAsync();
        var battle = await StartBattleAsync(client);

        var final = await PlayUntilFinishedAsync(client, battle.Id, "HolyShield");

        Assert.Equal(BattleStatus.Lost, final.Battle.Status);
        Assert.Equal(0, final.Battle.PlayerHp);
        Assert.Equal(MatchRulesEngine.LossGold, final.Reward!.GoldEarned);
        Assert.Equal(500 + MatchRulesEngine.LossGold, final.Reward.Player.Gold);
    }

    [Fact]
    public async Task PlayCard_AnotherPlayersBattleIsNotFound()
    {
        var owner = await SignedInClientAsync();
        var battle = await StartBattleAsync(owner);
        var intruder = await SignedInClientAsync();

        var response = await intruder.PostAsJsonAsync($"/api/battles/pve/{battle.Id}/turns", new { Card = "DragonClaw" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task PlayCard_RejectsUnknownCards()
    {
        var client = await SignedInClientAsync();
        var battle = await StartBattleAsync(client);

        var response = await client.PostAsJsonAsync($"/api/battles/pve/{battle.Id}/turns", new { Card = "InstantWin" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ClientReportedResultsAreNoLongerAccepted()
    {
        var client = await SignedInClientAsync();

        var response = await client.PostAsJsonAsync("/api/matches/pve/complete", new { IsVictory = true });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private async Task<HttpClient> SignedInClientAsync()
    {
        var client = _factory.CreateClient();
        var username = $"hero{Guid.NewGuid():N}"[..20];
        var response = await client.PostAsJsonAsync("/api/auth/register", new { Username = username, Password });
        response.EnsureSuccessStatusCode();
        var auth = await response.Content.ReadFromJsonAsync<AuthResponse>(Json);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth!.AccessToken);
        return client;
    }

    private static async Task<BattleStateResponse> StartBattleAsync(HttpClient client)
    {
        var response = await client.PostAsync("/api/battles/pve", null);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<BattleStateResponse>(Json))!;
    }

    private static async Task<PlayCardResponse> PlayAsync(HttpClient client, Guid battleId, string card)
    {
        var response = await client.PostAsJsonAsync($"/api/battles/pve/{battleId}/turns", new { Card = card });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<PlayCardResponse>(Json))!;
    }

    private static async Task<PlayCardResponse> PlayUntilFinishedAsync(HttpClient client, Guid battleId, string card)
    {
        for (var turn = 0; turn < 50; turn++)
        {
            var result = await PlayAsync(client, battleId, card);
            if (result.Battle.Status != BattleStatus.InProgress)
            {
                return result;
            }
        }

        throw new InvalidOperationException("Battle did not finish within 50 turns.");
    }
}
