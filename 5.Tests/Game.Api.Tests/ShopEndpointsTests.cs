using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Game.Api.Models;
using Game.Core.Battles;
using Game.Core.Entities;
using Game.Core.Services;
using Game.Core.Shop;
using Game.Infrastructure.Data;
using Microsoft.Extensions.DependencyInjection;

namespace Game.Api.Tests;

public class ShopEndpointsTests : IClassFixture<GameApiFactory>
{
    private const string Password = "Arena-Pass1";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly GameApiFactory _factory;

    public ShopEndpointsTests(GameApiFactory factory) => _factory = factory;

    [Fact]
    public async Task TheShopListsEveryItemPricedForThePlayer()
    {
        var client = await SignedInClientAsync();

        var shop = await client.GetFromJsonAsync<ShopResponse>("/api/shop", Json);

        Assert.Equal(Player.StartingGold, shop!.Gold);
        Assert.Equal(Enum.GetValues<ShopItem>().Length, shop.Offers.Count);
        Assert.Equal(150, shop.Offers.Single(o => o.Item == ShopItem.FireballUpgrade).Price);
        Assert.Equal(GoldShop.ElixirPrice, shop.Offers.Single(o => o.Item == ShopItem.BattleElixir).Price);
    }

    [Fact]
    public async Task Buying_SpendsGoldAndTheProfileShowsIt()
    {
        var client = await SignedInClientAsync();

        var response = await BuyAsync(client, ShopItem.DragonClawUpgrade);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var shop = (await response.Content.ReadFromJsonAsync<ShopResponse>(Json))!;
        Assert.Equal(Player.StartingGold - 150, shop.Gold);
        Assert.Equal(2, shop.Offers.Single(o => o.Item == ShopItem.DragonClawUpgrade).Owned);

        var me = await client.GetFromJsonAsync<PlayerProfileResponse>("/api/players/me", Json);
        Assert.Equal(Player.StartingGold - 150, me!.Gold);
    }

    [Fact]
    public async Task Buying_FailsWithAReasonWhenThePlayerRunsOutOfGold()
    {
        var (client, playerId) = await SignedInPlayerAsync();
        await _factory.GiveGoldAsync(playerId, 500 - Player.StartingGold);
        await BuyAsync(client, ShopItem.FireballUpgrade);   // 500 -> 350
        await BuyAsync(client, ShopItem.FireballUpgrade);   // 350 -> 50

        var response = await BuyAsync(client, ShopItem.HolyShieldUpgrade);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("150 gold", await response.Content.ReadAsStringAsync());
        var shop = await client.GetFromJsonAsync<ShopResponse>("/api/shop", Json);
        Assert.Equal(50, shop!.Gold);
    }

    [Fact]
    public async Task Buying_RejectsUnknownItems()
    {
        var client = await SignedInClientAsync();

        var response = await client.PostAsJsonAsync("/api/shop/purchases", new { Item = 99 });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task TheNextBossFightUsesUpgradesAndAnElixir()
    {
        var client = await SignedInClientAsync();
        await BuyAsync(client, ShopItem.FireballUpgrade);
        await BuyAsync(client, ShopItem.BattleElixir);

        var response = await client.PostAsync("/api/battles/pve", null);
        var battle = (await response.Content.ReadFromJsonAsync<BattleStateResponse>(Json))!;

        Assert.Equal(PveBattle.BasePlayerMaxHp + Player.ElixirBonusHp, battle.PlayerHp);
        Assert.Equal(battle.PlayerHp, battle.PlayerMaxHp);
        var fireball = battle.Cards.Single(c => c.Card == BattleCard.Fireball);
        Assert.Equal(2, fireball.Level);
        // A level-2 Fireball, plus the Sorcerer's class boost.
        Assert.Equal(HeroClasses.CardFor(HeroClass.Sorcerer, BattleCard.Fireball, 2).Damage, fireball.Damage);

        var turn = await client.PostAsJsonAsync($"/api/battles/pve/{battle.Id}/turns", new { Card = "Fireball" });
        var played = (await turn.Content.ReadFromJsonAsync<PlayCardResponse>(Json))!;
        Assert.Equal(fireball.Damage, played.TurnResult.DamageDealt);

        var shop = await client.GetFromJsonAsync<ShopResponse>("/api/shop", Json);
        Assert.Equal(0, shop!.Offers.Single(o => o.Item == ShopItem.BattleElixir).Owned);
    }

    [Fact]
    public async Task TheShopRequiresSignIn()
    {
        var response = await _factory.CreateClient().GetAsync("/api/shop");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Titles_UnlockWithDuelWinsAndCanBeShownOrHidden()
    {
        var (client, playerId) = await SignedInPlayerAsync();

        var locked = await BuyAsync(client, ShopItem.TitleDuelist);
        Assert.Equal(HttpStatusCode.BadRequest, locked.StatusCode);
        Assert.Contains("1 more duel", await locked.Content.ReadAsStringAsync());

        await GiveDuelWinsAsync(playerId, 1);
        var bought = await BuyAsync(client, ShopItem.TitleDuelist);
        Assert.Equal(HttpStatusCode.OK, bought.StatusCode);
        var shop = (await bought.Content.ReadFromJsonAsync<ShopResponse>(Json))!;
        Assert.Equal(PlayerTitle.Duelist, shop.EquippedTitle);
        Assert.Equal(1, shop.PvpWins);

        var hidden = await client.PutAsJsonAsync("/api/shop/title", new EquipTitleRequest(null), Json);
        Assert.Null((await hidden.Content.ReadFromJsonAsync<ShopResponse>(Json))!.EquippedTitle);

        var notOwned = await client.PutAsJsonAsync("/api/shop/title", new EquipTitleRequest(PlayerTitle.Gladiator), Json);
        Assert.Equal(HttpStatusCode.BadRequest, notOwned.StatusCode);
    }

    [Fact]
    public async Task TheLeaderboardShowsTitles()
    {
        var (client, playerId) = await SignedInPlayerAsync();
        await GiveDuelWinsAsync(playerId, 1);
        await BuyAsync(client, ShopItem.TitleDuelist);

        // The leaderboard ranks by PvP rating, so give this player a top rating to make the top 10.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var player = await db.Players.FindAsync(playerId);
            player!.RecordPvpWin(ratingGained: 10_000);
            await db.SaveChangesAsync();
        }

        var board = await client.GetFromJsonAsync<List<LeaderboardRow>>("/api/players/leaderboard", Json);

        Assert.Equal("the Duelist", board!.Single(r => r.Id == playerId).Title);
    }

    [Fact]
    public async Task TheLeaderboardRanksByRating()
    {
        var (client, playerId) = await SignedInPlayerAsync();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var player = await db.Players.FindAsync(playerId);
            player!.RecordPvpWin(ratingGained: 20_000);
            player.RecordPvpLoss(ratingLost: 0);
            await db.SaveChangesAsync();
        }

        var board = await client.GetFromJsonAsync<List<LeaderboardRow>>("/api/players/leaderboard", Json);

        Assert.Equal(board!.OrderByDescending(r => r.Rating).Select(r => r.Id), board.Select(r => r.Id));
        var top = board[0];
        Assert.Equal(playerId, top.Id);
        Assert.Equal(EloRating.StartingRating + 20_000, top.Rating);
        Assert.Equal(1, top.PvpLosses);
    }

    [Fact]
    public async Task TheLeaderboardCanShowOneClassesRecords()
    {
        var (client, playerId) = await SignedInPlayerAsync();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var player = await db.Players.FindAsync(playerId);
            for (var i = 0; i < 1_000; i++) player!.RecordPvpWin(ratingGained: 0, playedAs: HeroClass.Paladin);
            player!.RecordPvpLoss(ratingLost: 0, playedAs: HeroClass.Ranger);
            await db.SaveChangesAsync();
        }

        var paladins = await client.GetFromJsonAsync<List<LeaderboardRow>>("/api/players/leaderboard?class=Paladin", Json);
        var rangers = await client.GetFromJsonAsync<List<LeaderboardRow>>("/api/players/leaderboard?class=Ranger", Json);
        var sorcerers = await client.GetFromJsonAsync<List<LeaderboardRow>>("/api/players/leaderboard?class=Sorcerer", Json);

        Assert.Equal(playerId, paladins![0].Id);
        Assert.Equal((1_000, 0), (paladins[0].PvpWins, paladins[0].PvpLosses));
        var asRanger = rangers!.Single(r => r.Id == playerId);
        Assert.Equal((0, 1), (asRanger.PvpWins, asRanger.PvpLosses));
        Assert.DoesNotContain(sorcerers!, r => r.Id == playerId);
        Assert.Equal(paladins.OrderByDescending(r => r.PvpWins).Select(r => r.Id), paladins.Select(r => r.Id));
    }

    [Fact]
    public async Task Cosmetics_CanBeBoughtWornAndTakenOff()
    {
        var (client, playerId) = await SignedInPlayerAsync();
        await _factory.GiveGoldAsync(playerId, 500 - Player.StartingGold);

        var tooDear = await BuyAsync(client, ShopItem.FrameSilver);
        Assert.Equal(HttpStatusCode.BadRequest, tooDear.StatusCode);

        var bought = await BuyAsync(client, ShopItem.FrameBronze);
        Assert.Equal(HttpStatusCode.OK, bought.StatusCode);
        var shop = (await bought.Content.ReadFromJsonAsync<ShopResponse>(Json))!;
        Assert.Equal(Cosmetic.BronzeFrame, shop.EquippedFrame);
        Assert.Equal(0, shop.Gold);

        var me = await client.GetFromJsonAsync<PlayerProfileResponse>("/api/players/me", Json);
        Assert.Equal(Cosmetic.BronzeFrame, me!.Frame);

        var off = await client.PutAsJsonAsync("/api/shop/cosmetic", new EquipCosmeticRequest(CosmeticKind.AvatarFrame, null), Json);
        Assert.Null((await off.Content.ReadFromJsonAsync<ShopResponse>(Json))!.EquippedFrame);

        var notOwned = await client.PutAsJsonAsync("/api/shop/cosmetic", new EquipCosmeticRequest(CosmeticKind.CardSkin, Cosmetic.VoidCards), Json);
        Assert.Equal(HttpStatusCode.BadRequest, notOwned.StatusCode);
    }

    [Fact]
    public async Task TodaysBounties_ShowProgressAndTheWinStreak()
    {
        var (client, _) = await SignedInPlayerAsync();

        var bounties = await client.GetFromJsonAsync<BountiesResponse>("/api/bounties", Json);

        Assert.Equal(3, bounties!.Bounties.Count);
        Assert.All(bounties.Bounties, b => Assert.Equal(0, b.Progress));
        Assert.Equal(0, bounties.WinStreak);
        Assert.True(bounties.ResetsAt > DateTime.UtcNow);
    }

    private sealed record LeaderboardRow(Guid Id, string Username, string? Title, int Rating, int PvpWins, int PvpLosses);

    private async Task GiveDuelWinsAsync(Guid playerId, int wins)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var player = await db.Players.FindAsync(playerId);
        for (var i = 0; i < wins; i++) player!.RecordPvpWin(ratingGained: 0);
        await db.SaveChangesAsync();
    }

    private static Task<HttpResponseMessage> BuyAsync(HttpClient client, ShopItem item) =>
        client.PostAsJsonAsync("/api/shop/purchases", new PurchaseRequest(item), Json);

    private async Task<HttpClient> SignedInClientAsync() => (await SignedInPlayerAsync()).Client;

    private async Task<(HttpClient Client, Guid PlayerId)> SignedInPlayerAsync()
    {
        var client = _factory.CreateClient();
        var username = $"shop{Guid.NewGuid():N}"[..20];
        var response = await client.PostAsJsonAsync("/api/auth/register", new { Username = username, Password });
        response.EnsureSuccessStatusCode();
        var auth = (await response.Content.ReadFromJsonAsync<AuthResponse>(Json))!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        return (client, auth.Player.Id);
    }
}
