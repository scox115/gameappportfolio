using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Game.Api.Models;
using Game.Core.History;
using Game.Infrastructure.Data;
using Microsoft.Extensions.DependencyInjection;

namespace Game.Api.Tests;

// The middleware adds an Age header only to responses it serves from the cache.
public class OutputCachingTests
{
    private const string Password = "Arena-Pass1";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task TheLeaderboard_IsServedFromTheCache_EvenToSignedInPlayers()
    {
        using var factory = new GameApiFactory();
        var (client, _) = await RegisterAsync(factory);

        var first = await client.GetAsync("/api/v1/players/leaderboard");
        var second = await client.GetAsync("/api/v1/players/leaderboard");

        Assert.Null(first.Headers.Age);
        Assert.NotNull(second.Headers.Age);
        Assert.Equal(await first.Content.ReadAsStringAsync(), await second.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task EachClassLeaderboard_IsCachedSeparately()
    {
        using var factory = new GameApiFactory();
        var client = factory.CreateClient();

        await client.GetAsync("/api/v1/players/leaderboard?class=Paladin");
        var ranger = await client.GetAsync("/api/v1/players/leaderboard?class=Ranger");

        Assert.Null(ranger.Headers.Age);
    }

    [Fact]
    public async Task NewGold_ShowsOnTheLeaderboardAtOnce()
    {
        using var factory = new GameApiFactory();
        var (client, auth) = await RegisterAsync(factory);
        var before = await LeaderboardGoldAsync(client, auth.Player.Id);
        await LeaderboardGoldAsync(client, auth.Player.Id); // now cached

        await factory.GiveGoldAsync(auth.Player.Id, 250);

        Assert.Equal(before + 250, await LeaderboardGoldAsync(client, auth.Player.Id));
    }

    [Fact]
    public async Task ANewHero_IsCountedAtOnce()
    {
        using var factory = new GameApiFactory();
        var client = factory.CreateClient();
        var before = await client.GetFromJsonAsync<PlayerStatsResponse>("/api/v1/players/stats", Json);

        await RegisterAsync(factory);

        var after = await client.GetFromJsonAsync<PlayerStatsResponse>("/api/v1/players/stats", Json);
        Assert.Equal(before!.RegisteredPlayers + 1, after!.RegisteredPlayers);
    }

    [Fact]
    public async Task ArenaStats_AreEvictedWhenTheMatchConsumerSavesNewTotals()
    {
        using var factory = new GameApiFactory();
        var client = factory.CreateClient();
        await client.GetAsync("/api/v1/arena/stats?days=7");
        Assert.NotNull((await client.GetAsync("/api/v1/arena/stats?days=7")).Headers.Age);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.DailyArenaStats.Add(new DailyArenaStats(DateOnly.FromDateTime(factory.Clock.GetUtcNow().UtcDateTime)));
            await db.SaveChangesAsync();
        }

        Assert.Null((await client.GetAsync("/api/v1/arena/stats?days=7")).Headers.Age);
    }

    [Fact]
    public async Task APlayersOwnProfile_IsNeverCached()
    {
        using var factory = new GameApiFactory();
        var (client, _) = await RegisterAsync(factory);

        await client.GetAsync("/api/v1/players/me");
        var second = await client.GetAsync("/api/v1/players/me");

        Assert.Null(second.Headers.Age);
    }

    private static async Task<int> LeaderboardGoldAsync(HttpClient client, Guid playerId)
    {
        var rows = await client.GetFromJsonAsync<List<JsonElement>>("/api/v1/players/leaderboard", Json);
        return rows!.Single(row => row.GetProperty("id").GetGuid() == playerId).GetProperty("gold").GetInt32();
    }

    private static async Task<(HttpClient Client, AuthResponse Auth)> RegisterAsync(GameApiFactory factory)
    {
        var client = factory.CreateClient();
        var username = $"hero{Guid.NewGuid():N}"[..20];
        var response = await client.PostAsJsonAsync("/api/v1/auth/register", new { Username = username, Password });
        response.EnsureSuccessStatusCode();
        var auth = (await response.Content.ReadFromJsonAsync<AuthResponse>(Json))!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        return (client, auth);
    }
}
