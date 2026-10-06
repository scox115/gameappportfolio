using Game.Core.Entities;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Game.Api.Models;
using Game.Core.Battles;
using Game.Core.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Game.Api.Tests;

// Battles run on the server with FixedBattleRandom, so every roll favours the player unless a test says otherwise.
public class BattleEndpointsTests : IClassFixture<GameApiFactory>
{
    private const string Password = "Arena-Pass1";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly GameApiFactory _factory;

    // New heroes are Sorcerers, whose class boosts Fireball.
    private static readonly int Fireball = HeroClasses.CardFor(HeroClass.Sorcerer, BattleCard.Fireball, 1).Damage;

    public BattleEndpointsTests(GameApiFactory factory) => _factory = factory;

    [Fact]
    public async Task StartBattle_CreatesABattleAtFullHealth()
    {
        var client = await SignedInClientAsync();

        var response = await client.PostAsync("/api/v1/battles/pve", null);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var battle = await response.Content.ReadFromJsonAsync<BattleStateResponse>(Json);
        Assert.Equal(PveBattle.BasePlayerMaxHp, battle!.PlayerHp);
        Assert.Equal(PveBattle.BossMaxHp, battle.BossHp);
        Assert.Equal(BattleStatus.InProgress, battle.Status);
    }

    [Fact]
    public async Task StartBattle_ResumesTheUnfinishedBattleInsteadOfStartingOver()
    {
        var client = await SignedInClientAsync();
        var first = await StartBattleAsync(client);
        await PlayAsync(client, first.Id, "Fireball");

        var response = await client.PostAsync("/api/v1/battles/pve", null);
        var resumed = await response.Content.ReadFromJsonAsync<BattleStateResponse>(Json);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(first.Id, resumed!.Id);
        Assert.Equal(PveBattle.BossMaxHp - Fireball, resumed.BossHp);
    }

    [Fact]
    public async Task PlayCard_ServerAppliesTheCardAndTheBossAttack()
    {
        var client = await SignedInClientAsync();
        var battle = await StartBattleAsync(client);

        var result = await PlayAsync(client, battle.Id, "Fireball");

        Assert.False(result.TurnResult.CardFailed);
        Assert.Equal(Fireball, result.TurnResult.DamageDealt);
        Assert.Equal("Slash", result.TurnResult.BossMoveName);
        Assert.Equal(PveBattle.OpeningBossAttack, result.TurnResult.BossDamage);
        Assert.Equal(PveBattle.BossMaxHp - Fireball, result.Battle.BossHp);
        Assert.Equal(85, result.Battle.PlayerHp);
        Assert.Equal("Slash", result.Battle.BossNextMoveName);
        Assert.Null(result.Reward);
    }

    [Fact]
    public async Task HolyShield_BlocksTheBossAttackThenRecharges()
    {
        var client = await SignedInClientAsync();
        var battle = await StartBattleAsync(client);

        var result = await PlayAsync(client, battle.Id, "HolyShield");

        Assert.True(result.TurnResult.AttackBlocked);
        Assert.Equal(0, result.TurnResult.BossDamage);
        Assert.Equal(BattleCard.HolyShield, result.Battle.RechargingCard);

        var again = await client.PostAsJsonAsync($"/api/v1/battles/pve/{battle.Id}/turns", new { Card = "HolyShield" });
        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);
    }

    [Fact]
    public async Task WinningABattle_PaysTheWinRewardOnceAndClosesTheBattle()
    {
        var client = await SignedInClientAsync();
        var battle = await StartBattleAsync(client);

        var final = await PlayUntilFinishedAsync(client, battle.Id);

        Assert.Equal(BattleStatus.Won, final.Battle.Status);
        // A first win has no streak bonus; today's bounties may include "Defeat the Shadow Overlord".
        var bountyGold = final.Reward!.BountiesCompleted!.Sum(b => b.Reward);
        Assert.Equal(0, final.Reward.StreakBonus);
        Assert.Equal(MatchRulesEngine.WinGold + bountyGold, final.Reward.GoldEarned);
        var me = await client.GetFromJsonAsync<PlayerProfileResponse>("/api/v1/players/me", Json);
        Assert.Equal(Player.StartingGold + MatchRulesEngine.WinGold + bountyGold, me!.Gold);
        Assert.Equal(1, me.WinStreak);
        Assert.False(final.Reward.ReducedBossReward);
        Assert.Equal(Player.FullRewardBossWinsPerDay - 1, final.Reward.FullRewardBossWinsLeft);

        var extraMove = await client.PostAsJsonAsync($"/api/v1/battles/pve/{battle.Id}/turns", new { Card = "Fireball" });
        Assert.Equal(HttpStatusCode.Conflict, extraMove.StatusCode);

        var next = await client.PostAsync("/api/v1/battles/pve", null);
        Assert.Equal(HttpStatusCode.Created, next.StatusCode);
    }

    [Fact]
    public async Task LosingABattle_PaysOnlyTheConsolationReward()
    {
        // Every card fails and the boss drains its health back, so the fight can't be won.
        using var unluckyFactory = _factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IBattleRandom>();
            services.AddSingleton<IBattleRandom>(new FixedBattleRandom(lucky: false));
        }));
        var client = await SignedInClientAsync(unluckyFactory);
        var battle = await StartBattleAsync(client);

        var final = await PlayUntilFinishedAsync(client, battle.Id);

        Assert.Equal(BattleStatus.Lost, final.Battle.Status);
        Assert.Equal(0, final.Battle.PlayerHp);
        Assert.Equal(MatchRulesEngine.LossGold, final.Reward!.GoldEarned);
        Assert.Equal(Player.StartingGold + MatchRulesEngine.LossGold, final.Reward.Player.Gold);
    }

    [Fact]
    public async Task PlayCard_AnotherPlayersBattleIsNotFound()
    {
        var owner = await SignedInClientAsync();
        var battle = await StartBattleAsync(owner);
        var intruder = await SignedInClientAsync();

        var response = await intruder.PostAsJsonAsync($"/api/v1/battles/pve/{battle.Id}/turns", new { Card = "DragonClaw" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task PlayCard_RejectsUnknownCards()
    {
        var client = await SignedInClientAsync();
        var battle = await StartBattleAsync(client);

        var response = await client.PostAsJsonAsync($"/api/v1/battles/pve/{battle.Id}/turns", new { Card = "InstantWin" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ClientReportedResultsAreNoLongerAccepted()
    {
        var client = await SignedInClientAsync();

        var response = await client.PostAsJsonAsync("/api/v1/matches/pve/complete", new { IsVictory = true });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task HeroicBoss_IsLockedUntilEveryCardIsMaxedOut()
    {
        var client = await SignedInClientAsync();

        var response = await client.PostAsync("/api/v1/battles/pve?difficulty=Heroic", null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var status = await client.GetFromJsonAsync<BountiesResponse>("/api/v1/bounties", Json);
        Assert.False(status!.HeroicUnlocked);
    }

    [Fact]
    public async Task HeroicBoss_FirstWinTodayPaysTheHeroicReward_LaterWinsPayLess()
    {
        var client = await MaxedOutClientAsync();
        var status = await client.GetFromJsonAsync<BountiesResponse>("/api/v1/bounties", Json);
        Assert.True(status!.HeroicUnlocked);
        Assert.True(status.HeroicRewardAvailable);

        var battle = await StartBattleAsync(client, "?difficulty=Heroic");
        Assert.Equal(BossDifficulty.Heroic, battle.Difficulty);
        Assert.Equal(BossProfile.Heroic.MaxHp, battle.BossHp);
        Assert.Equal(BossProfile.Heroic.MaxHp, battle.BossMaxHp);
        Assert.Equal(BossProfile.Heroic.Name, battle.BossName);

        // Even with every roll going the hero's way, the Heroic boss needs a well-timed shield.
        var first = await PlayUntilFinishedAsync(client, battle.Id, shieldWhenInDanger: true);
        Assert.Equal(BattleStatus.Won, first.Battle.Status);
        var bonuses = first.Reward!.StreakBonus + first.Reward.BountiesCompleted!.Sum(b => b.Reward);
        Assert.Equal(MatchRulesEngine.HeroicWinGold + bonuses, first.Reward.GoldEarned);
        Assert.Equal(MatchRulesEngine.HeroicWinExperience, first.Reward.ExperienceEarned);
        Assert.False(first.Reward.ReducedBossReward);
        Assert.Null(first.Reward.FullRewardBossWinsLeft);

        var again = await StartBattleAsync(client, "?difficulty=Heroic");
        var second = await PlayUntilFinishedAsync(client, again.Id, shieldWhenInDanger: true);
        Assert.Equal(BattleStatus.Won, second.Battle.Status);
        Assert.True(second.Reward!.ReducedBossReward);

        // The normal boss's full-reward wins are untouched.
        status = await client.GetFromJsonAsync<BountiesResponse>("/api/v1/bounties", Json);
        Assert.False(status!.HeroicRewardAvailable);
        Assert.Equal(Player.FullRewardBossWinsPerDay, status.FullRewardBossWinsLeft);
    }

    // A hero who has bought every card upgrade in the Gold Shop.
    private async Task<HttpClient> MaxedOutClientAsync()
    {
        var client = await SignedInClientAsync();
        var me = await client.GetFromJsonAsync<PlayerProfileResponse>("/api/v1/players/me", Json);
        await _factory.GiveGoldAsync(me!.Id, 2000);
        foreach (var item in new[] { "FireballUpgrade", "HolyShieldUpgrade", "DragonClawUpgrade" })
        {
            for (var level = 1; level < BattleCards.MaxLevel; level++)
            {
                var bought = await client.PostAsJsonAsync("/api/v1/shop/purchases", new { Item = item });
                bought.EnsureSuccessStatusCode();
            }
        }
        return client;
    }

    private Task<HttpClient> SignedInClientAsync() => SignedInClientAsync(_factory);

    private static async Task<HttpClient> SignedInClientAsync(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        var username = $"hero{Guid.NewGuid():N}"[..20];
        var response = await client.PostAsJsonAsync("/api/v1/auth/register", new { Username = username, Password });
        response.EnsureSuccessStatusCode();
        var auth = await response.Content.ReadFromJsonAsync<AuthResponse>(Json);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth!.AccessToken);
        return client;
    }

    private static async Task<BattleStateResponse> StartBattleAsync(HttpClient client, string query = "")
    {
        var response = await client.PostAsync("/api/v1/battles/pve" + query, null);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<BattleStateResponse>(Json))!;
    }

    private static async Task<PlayCardResponse> PlayAsync(HttpClient client, Guid battleId, string card)
    {
        var response = await client.PostAsJsonAsync($"/api/v1/battles/pve/{battleId}/turns", new { Card = card });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<PlayCardResponse>(Json))!;
    }

    // Plays Dragon Claw whenever it is ready and Fireball while it recharges. With shieldWhenInDanger
    // it raises Holy Shield when the boss's next move would finish the hero.
    private static async Task<PlayCardResponse> PlayUntilFinishedAsync(HttpClient client, Guid battleId, bool shieldWhenInDanger = false)
    {
        BattleCard? recharging = null;
        var inDanger = false;
        for (var turn = 0; turn < 50; turn++)
        {
            var card = shieldWhenInDanger && inDanger && recharging != BattleCard.HolyShield ? "HolyShield"
                : recharging == BattleCard.DragonClaw ? "Fireball" : "DragonClaw";
            var result = await PlayAsync(client, battleId, card);
            if (result.Battle.Status != BattleStatus.InProgress)
            {
                return result;
            }
            recharging = result.Battle.RechargingCard;
            inDanger = result.Battle.BossNextAttack >= result.Battle.PlayerHp;
        }

        throw new InvalidOperationException("Battle did not finish within 50 turns.");
    }
}
