using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Game.Api.Models;

namespace Game.Api.Tests;

public class AuthAndMatchEndpointsTests : IClassFixture<GameApiFactory>
{
    private const string Password = "Arena-Pass1";
    private readonly GameApiFactory _factory;

    public AuthAndMatchEndpointsTests(GameApiFactory factory) => _factory = factory;

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
        Assert.Equal(500, auth.Player.Gold);
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
    [InlineData("POST", "/api/matches/pve/complete")]
    [InlineData("POST", "/api/matches/complete")]
    public async Task GameEndpoints_RequireASignedInPlayer(string method, string url)
    {
        var client = _factory.CreateClient();

        var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), url)
        {
            Content = method == "POST" ? JsonContent.Create(new { IsVictory = true }) : null
        });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task PveComplete_PaysTheSignedInPlayer()
    {
        var client = _factory.CreateClient();
        var auth = await RegisterAsync(client, NewUsername());
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);

        var response = await client.PostAsJsonAsync("/api/matches/pve/complete", new { IsVictory = true });
        var me = await client.GetFromJsonAsync<PlayerProfileResponse>("/api/players/me");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(auth.Player.Id, me!.Id);
        Assert.Equal(600, me.Gold);
    }

    [Fact]
    public async Task PvpComplete_ForbidsReportingAMatchYouWereNotIn()
    {
        var client = _factory.CreateClient();
        var caller = await RegisterAsync(client, NewUsername());
        var winner = await RegisterAsync(client, NewUsername());
        var loser = await RegisterAsync(client, NewUsername());
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", caller.AccessToken);

        var response = await client.PostAsJsonAsync("/api/matches/complete",
            new { WinnerPlayerId = winner.Player.Id, LoserPlayerId = loser.Player.Id });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ForgedTokensAreRejected()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "not-a-real-token");

        var response = await client.GetAsync("/api/players/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static async Task<AuthResponse> RegisterAsync(HttpClient client, string username)
    {
        var response = await client.PostAsJsonAsync("/api/auth/register", new { Username = username, Password });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private static string NewUsername() => $"hero{Guid.NewGuid():N}"[..20];
}
