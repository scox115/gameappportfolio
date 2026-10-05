using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Game.Api.Auth;
using Game.Api.Hubs;
using Game.Api.Models;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;

namespace Game.Api.Tests;

// An account is signed in on one browser at a time: signing in again retires the older session.
public class SingleSessionTests : IClassFixture<GameApiFactory>
{
    private const string Password = "Arena-Pass1";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private readonly GameApiFactory _factory;

    public SingleSessionTests(GameApiFactory factory) => _factory = factory;

    [Fact]
    public async Task SigningInAgain_SignsOutTheFirstBrowser()
    {
        var username = NewUsername();
        var first = await RegisterAsync(username);
        var second = await LoginAsync(username);

        var oldBrowser = await GetMeAsync(first.AccessToken);
        var newBrowser = await GetMeAsync(second.AccessToken);

        Assert.Equal(HttpStatusCode.Unauthorized, oldBrowser.StatusCode);
        Assert.Equal(SessionClaims.SignedInElsewhere, oldBrowser.Headers.GetValues(SessionClaims.EndedHeader).Single());
        Assert.Equal(HttpStatusCode.OK, newBrowser.StatusCode);
    }

    [Fact]
    public async Task TheFirstBrowsersRefreshToken_StopsWorkingWithoutEndingTheNewSession()
    {
        var username = NewUsername();
        var first = await RegisterAsync(username);
        var second = await LoginAsync(username);
        var client = _factory.CreateClient();

        var oldRefresh = await client.PostAsJsonAsync("/api/auth/refresh", new { first.RefreshToken });
        var newRefresh = await client.PostAsJsonAsync("/api/auth/refresh", new { second.RefreshToken });

        Assert.Equal(HttpStatusCode.Unauthorized, oldRefresh.StatusCode);
        Assert.Equal(SessionClaims.SignedInElsewhere, oldRefresh.Headers.GetValues(SessionClaims.EndedHeader).Single());
        Assert.Equal(HttpStatusCode.OK, newRefresh.StatusCode);
    }

    [Fact]
    public async Task TheFirstBrowserIsToldStraightAway()
    {
        var username = NewUsername();
        var first = await RegisterAsync(username);
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // WebSockets, as browsers use: with long polling the old token can be refused on the next
        // poll before the message is picked up, which made this test flaky.
        await using var connection = BuildWebSocketConnection(SessionHub.Path, first.AccessToken);
        connection.On(nameof(ISessionClient.SessionEnded), () => ended.TrySetResult());
        await connection.StartAsync();

        await LoginAsync(username);

        await ended.Task.WaitAsync(Wait);
    }

    [Fact]
    public async Task AnOpenArenaConnection_FromTheOldSessionIsRefused()
    {
        var username = NewUsername();
        var first = await RegisterAsync(username);
        // Over WebSockets the token is only checked when the connection opens, so the hub has to re-check it.
        await using var connection = BuildWebSocketConnection(ArenaHub.Path, first.AccessToken);
        await connection.StartAsync();

        await LoginAsync(username);

        var error = await Assert.ThrowsAnyAsync<Exception>(() => connection.InvokeAsync<bool>(nameof(ArenaHub.FindOpponent)));
        Assert.Contains("another browser", error.Message);
    }

    private async Task<HttpResponseMessage> GetMeAsync(string accessToken)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return await client.GetAsync("/api/players/me");
    }

    private Task<AuthResponse> RegisterAsync(string username) => SignInAsync("/api/auth/register", username);

    private Task<AuthResponse> LoginAsync(string username) => SignInAsync("/api/auth/login", username);

    private async Task<AuthResponse> SignInAsync(string url, string username)
    {
        var response = await _factory.CreateClient().PostAsJsonAsync(url, new { Username = username, Password });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private HubConnection BuildWebSocketConnection(string path, string accessToken) =>
        new HubConnectionBuilder()
            .WithUrl(new Uri(_factory.Server.BaseAddress, path), options =>
            {
                options.Transports = HttpTransportType.WebSockets;
                options.SkipNegotiation = true;
                options.WebSocketFactory = async (context, cancellationToken) =>
                {
                    var uri = new UriBuilder(context.Uri) { Query = $"access_token={Uri.EscapeDataString(accessToken)}" }.Uri;
                    return await _factory.Server.CreateWebSocketClient().ConnectAsync(uri, cancellationToken);
                };
            })
            .Build();

    private static string NewUsername() => $"solo{Guid.NewGuid():N}"[..20];
}
