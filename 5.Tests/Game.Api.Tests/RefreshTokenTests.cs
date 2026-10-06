using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Game.Api.Models;
using Game.Infrastructure.Identity;

namespace Game.Api.Tests;

public class RefreshTokenTests : IClassFixture<GameApiFactory>
{
    private const string Password = "Arena-Pass1";
    private readonly GameApiFactory _factory;

    public RefreshTokenTests(GameApiFactory factory) => _factory = factory;

    [Fact]
    public async Task SigningIn_ReturnsARefreshToken()
    {
        var auth = await RegisterAsync();

        Assert.False(string.IsNullOrEmpty(auth.RefreshToken));
        Assert.True(auth.RefreshTokenExpiresAt > auth.ExpiresAt);
    }

    [Fact]
    public async Task Refresh_ReturnsANewWorkingAccessTokenAndRefreshToken()
    {
        var auth = await RegisterAsync();
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/v1/auth/refresh", new { auth.RefreshToken });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var refreshed = (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
        Assert.NotEqual(auth.RefreshToken, refreshed.RefreshToken);
        Assert.Equal(auth.Player.Id, refreshed.Player.Id);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", refreshed.AccessToken);
        var me = await client.GetAsync("/api/v1/players/me");
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
    }

    [Fact]
    public async Task Refresh_RejectsUnknownTokens()
    {
        var response = await _factory.CreateClient().PostAsJsonAsync("/api/v1/auth/refresh", new { RefreshToken = "not-a-real-token" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ReusingAReplacedToken_RevokesEverySessionForThatPlayer()
    {
        var auth = await RegisterAsync();
        var client = _factory.CreateClient();
        var first = await RefreshAsync(client, auth.RefreshToken);

        // The original token was already used, so presenting it again looks like theft.
        var reused = await client.PostAsJsonAsync("/api/v1/auth/refresh", new { auth.RefreshToken });
        Assert.Equal(HttpStatusCode.Unauthorized, reused.StatusCode);

        // That also kills the token issued by the legitimate refresh.
        var afterReuse = await client.PostAsJsonAsync("/api/v1/auth/refresh", new { first.RefreshToken });
        Assert.Equal(HttpStatusCode.Unauthorized, afterReuse.StatusCode);
    }

    [Fact]
    public async Task Logout_RevokesTheRefreshToken()
    {
        var auth = await RegisterAsync();
        var client = _factory.CreateClient();

        var logout = await client.PostAsJsonAsync("/api/v1/auth/logout", new { auth.RefreshToken });
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);

        var refresh = await client.PostAsJsonAsync("/api/v1/auth/refresh", new { auth.RefreshToken });
        Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);
    }

    [Fact]
    public void RefreshToken_IsInactiveOnceExpiredOrRevoked()
    {
        var now = new DateTime(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc);
        var token = new RefreshToken(Guid.NewGuid(), "hash", now, now.AddDays(7));

        Assert.True(token.IsActive(now.AddDays(6)));
        Assert.False(token.IsActive(now.AddDays(7)));

        token.Revoke(now.AddHours(1));
        Assert.False(token.IsActive(now.AddHours(2)));
    }

    private async Task<AuthResponse> RegisterAsync()
    {
        var username = $"hero{Guid.NewGuid():N}"[..20];
        var response = await _factory.CreateClient().PostAsJsonAsync("/api/v1/auth/register", new { Username = username, Password });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private static async Task<AuthResponse> RefreshAsync(HttpClient client, string refreshToken)
    {
        var response = await client.PostAsJsonAsync("/api/v1/auth/refresh", new { RefreshToken = refreshToken });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }
}
