using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Game.Api.Models;
using Game.Core.Battles;

namespace Game.Api.Tests;

public class ClassEndpointsTests : IClassFixture<GameApiFactory>
{
    private const string Password = "Arena-Pass1";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly GameApiFactory _factory;

    public ClassEndpointsTests(GameApiFactory factory) => _factory = factory;

    [Fact]
    public async Task ListsTheThreeClasses()
    {
        var classes = await _factory.CreateClient().GetFromJsonAsync<List<HeroClassResponse>>("/api/v1/classes", Json);

        Assert.Equal([HeroClass.Sorcerer, HeroClass.Paladin, HeroClass.Ranger], classes!.Select(c => c.Class));
        Assert.All(classes, c => Assert.Equal(HeroClasses.ChangePrice, c.ChangePrice));
    }

    [Fact]
    public async Task Register_WithAClass_FightsAsThatClass()
    {
        var (client, auth) = await RegisterAsync(HeroClass.Ranger);
        Assert.Equal(HeroClass.Ranger, auth.Player.Class);

        var response = await client.PostAsync("/api/v1/battles/pve", null);
        var battle = (await response.Content.ReadFromJsonAsync<BattleStateResponse>(Json))!;

        Assert.Equal(20, battle.Cards.Single(c => c.Card == BattleCard.DragonClaw).FailChance);
    }

    [Fact]
    public async Task Register_WithoutAClass_IsASorcerer()
    {
        var (_, auth) = await RegisterAsync(null);

        Assert.Equal(HeroClass.Sorcerer, auth.Player.Class);
    }

    [Fact]
    public async Task ChangingClass_CostsGold()
    {
        var (client, auth) = await RegisterAsync(null);
        await _factory.GiveGoldAsync(auth.Player.Id, HeroClasses.ChangePrice);

        var response = await client.PutAsJsonAsync("/api/v1/players/me/class", new { Class = "Paladin" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var me = await response.Content.ReadFromJsonAsync<PlayerProfileResponse>(Json);
        Assert.Equal(HeroClass.Paladin, me!.Class);
        Assert.Equal(auth.Player.Gold, me.Gold);
    }

    [Fact]
    public async Task ChangingClass_WithoutEnoughGold_IsRefused()
    {
        var (client, auth) = await RegisterAsync(null);

        var response = await client.PutAsJsonAsync("/api/v1/players/me/class", new { Class = "Paladin" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var me = await client.GetFromJsonAsync<PlayerProfileResponse>("/api/v1/players/me", Json);
        Assert.Equal(HeroClass.Sorcerer, me!.Class);
        Assert.Equal(auth.Player.Gold, me.Gold);
    }

    [Fact]
    public async Task ChangingClass_NeedsSigningIn()
    {
        var response = await _factory.CreateClient().PutAsJsonAsync("/api/v1/players/me/class", new { Class = "Paladin" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private async Task<(HttpClient Client, AuthResponse Auth)> RegisterAsync(HeroClass? heroClass)
    {
        var client = _factory.CreateClient();
        var username = $"hero{Guid.NewGuid():N}"[..20];
        var response = await client.PostAsJsonAsync("/api/v1/auth/register", new { Username = username, Password, Class = heroClass?.ToString() });
        response.EnsureSuccessStatusCode();
        var auth = (await response.Content.ReadFromJsonAsync<AuthResponse>(Json))!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        return (client, auth);
    }
}
