using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Game.Api.Models;
using Game.Core.Battles;
using Game.Core.Entities;
using Game.Core.Shop;

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

        Assert.Equal(500, shop!.Gold);
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
        Assert.Equal(350, shop.Gold);
        Assert.Equal(2, shop.Offers.Single(o => o.Item == ShopItem.DragonClawUpgrade).Owned);

        var me = await client.GetFromJsonAsync<PlayerProfileResponse>("/api/players/me", Json);
        Assert.Equal(350, me!.Gold);
    }

    [Fact]
    public async Task Buying_FailsWithAReasonWhenThePlayerRunsOutOfGold()
    {
        var client = await SignedInClientAsync();
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
        Assert.Equal(25, fireball.Damage);

        var turn = await client.PostAsJsonAsync($"/api/battles/pve/{battle.Id}/turns", new { Card = "Fireball" });
        var played = (await turn.Content.ReadFromJsonAsync<PlayCardResponse>(Json))!;
        Assert.Equal(25, played.TurnResult.DamageDealt);

        var shop = await client.GetFromJsonAsync<ShopResponse>("/api/shop", Json);
        Assert.Equal(0, shop!.Offers.Single(o => o.Item == ShopItem.BattleElixir).Owned);
    }

    [Fact]
    public async Task TheShopRequiresSignIn()
    {
        var response = await _factory.CreateClient().GetAsync("/api/shop");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static Task<HttpResponseMessage> BuyAsync(HttpClient client, ShopItem item) =>
        client.PostAsJsonAsync("/api/shop/purchases", new PurchaseRequest(item), Json);

    private async Task<HttpClient> SignedInClientAsync()
    {
        var client = _factory.CreateClient();
        var username = $"shop{Guid.NewGuid():N}"[..20];
        var response = await client.PostAsJsonAsync("/api/auth/register", new { Username = username, Password });
        response.EnsureSuccessStatusCode();
        var auth = (await response.Content.ReadFromJsonAsync<AuthResponse>(Json))!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        return client;
    }
}
