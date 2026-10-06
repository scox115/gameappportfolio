using Game.Core.Entities;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Game.Api.Models;

namespace Game.Api.Tests;

public class AuthEndpointsTests : IClassFixture<GameApiFactory>
{
    private const string Password = "Arena-Pass1";
    private readonly GameApiFactory _factory;

    public AuthEndpointsTests(GameApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Register_CreatesPlayerWithStartingGoldAndSignsIn()
    {
        var client = _factory.CreateClient();
        var username = NewUsername();

        var response = await client.PostAsJsonAsync("/api/auth/register", new { Username = username, Password });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var auth = await response.Content.ReadFromJsonAsync<AuthResponse>();
        Assert.False(string.IsNullOrEmpty(auth!.AccessToken));
        Assert.Equal(username, auth.Player.Username);
        Assert.Equal(Player.StartingGold, auth.Player.Gold);
    }

    [Fact]
    public async Task Register_RejectsDuplicateUsername()
    {
        var client = _factory.CreateClient();
        var username = NewUsername();
        await RegisterAsync(client, username);

        var response = await client.PostAsJsonAsync("/api/auth/register", new { Username = username, Password });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Register_RejectsWeakPassword()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/auth/register", new { Username = NewUsername(), Password = "short" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Login_RejectsWrongPasswordAndAcceptsTheRightOne()
    {
        var client = _factory.CreateClient();
        var username = NewUsername();
        await RegisterAsync(client, username);

        var wrong = await client.PostAsJsonAsync("/api/auth/login", new { Username = username, Password = "Wrong-Pass1" });
        var right = await client.PostAsJsonAsync("/api/auth/login", new { Username = username, Password });

        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal(HttpStatusCode.OK, right.StatusCode);
    }

    [Fact]
    public async Task Login_UnknownUserGetsSameResponseAsWrongPassword()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/auth/login", new { Username = NewUsername(), Password });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Login_LocksAccountAfterRepeatedFailures()
    {
        var client = _factory.CreateClient();
        var username = NewUsername();
        await RegisterAsync(client, username);

        for (var i = 0; i < 5; i++)
        {
            await client.PostAsJsonAsync("/api/auth/login", new { Username = username, Password = "Wrong-Pass1" });
        }
        var response = await client.PostAsJsonAsync("/api/auth/login", new { Username = username, Password });

        Assert.Equal(HttpStatusCode.Locked, response.StatusCode);
    }

    [Theory]
    [InlineData("GET", "/api/players/me")]
    [InlineData("POST", "/api/battles/pve")]
    [InlineData("POST", "/api/battles/pve/00000000-0000-0000-0000-000000000001/turns")]
    public async Task GameEndpoints_RequireASignedInPlayer(string method, string url)
    {
        var client = _factory.CreateClient();

        var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), url)
        {
            Content = method == "POST" ? JsonContent.Create(new { Card = "Fireball" }) : null
        });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ForgedTokensAreRejected()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "not-a-real-token");

        var response = await client.GetAsync("/api/players/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task PlayerStats_CountsEveryRegisteredHero()
    {
        var client = _factory.CreateClient();
        var before = await client.GetFromJsonAsync<PlayerStatsResponse>("/api/players/stats");

        await RegisterAsync(client, NewUsername());
        var after = await client.GetFromJsonAsync<PlayerStatsResponse>("/api/players/stats");

        Assert.Equal(before!.RegisteredPlayers + 1, after!.RegisteredPlayers);
    }

    private static async Task<AuthResponse> RegisterAsync(HttpClient client, string username)
    {
        var response = await client.PostAsJsonAsync("/api/auth/register", new { Username = username, Password });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private static string NewUsername() => $"hero{Guid.NewGuid():N}"[..20];
}
