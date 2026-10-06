using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Game.Api.Features;
using Game.Api.Hubs;
using Game.Api.Models;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;

namespace Game.Api.Tests;

public class FeatureFlagTests
{
    private const string Password = "Arena-Pass1";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task EverythingIsOn_ByDefault()
    {
        using var factory = new GameApiFactory();

        var features = await factory.CreateClient().GetFromJsonAsync<FeaturesResponse>("/api/v1/features", Json);

        Assert.Equal(new FeaturesResponse(Duels: true, HeroicBoss: true, GoldShop: true), features);
    }

    [Fact]
    public async Task WithTheGoldShopOff_TheShopAnswers503_AndTheClientIsTold()
    {
        using var factory = WithSettings(("FeatureManagement:GoldShop", "false"));
        var client = await SignedInClientAsync(factory);

        var response = await client.GetAsync("/api/v1/shop");
        var features = await client.GetFromJsonAsync<FeaturesResponse>("/api/v1/features", Json);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("The Gold Shop is switched off", await response.Content.ReadAsStringAsync());
        Assert.False(features!.GoldShop);
        Assert.True(features.Duels);
    }

    [Fact]
    public async Task WithTheHeroicBossOff_OnlyHeroicFightsAreRefused()
    {
        using var factory = WithSettings(("FeatureManagement:HeroicBoss", "false"));
        var client = await SignedInClientAsync(factory);

        var heroic = await client.PostAsync("/api/v1/battles/pve?difficulty=Heroic", null);
        var normal = await client.PostAsync("/api/v1/battles/pve", null);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, heroic.StatusCode);
        Assert.Equal(HttpStatusCode.Created, normal.StatusCode);
    }

    [Fact]
    public async Task WithDuelsOff_TheLobbyRefusesNewDuels()
    {
        using var factory = WithSettings(("FeatureManagement:Duels", "false"));
        var (_, token) = await RegisterAsync(factory);
        await using var hub = new HubConnectionBuilder()
            .WithUrl(new Uri(factory.Server.BaseAddress, ArenaHub.Path), options =>
            {
                options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
                options.AccessTokenProvider = () => Task.FromResult<string?>(token);
            })
            .Build();
        await hub.StartAsync();

        var error = await Assert.ThrowsAsync<HubException>(() => hub.InvokeAsync<bool>("FindOpponent"));

        Assert.Contains("Duels are switched off", error.Message);
    }

    [Fact]
    public async Task AFlagFromAzureAppConfiguration_OverridesTheAppsettingsDefault()
    {
        // App Configuration delivers flags in the Microsoft feature flag schema; this is what it adds.
        using var factory = WithSettings(
            ("feature_management:feature_flags:0:id", GameFeatures.Duels),
            ("feature_management:feature_flags:0:enabled", "false"));

        var features = await factory.CreateClient().GetFromJsonAsync<FeaturesResponse>("/api/v1/features", Json);

        Assert.False(features!.Duels);
        Assert.True(features.GoldShop);
    }

    private static WebApplicationFactory<Program> WithSettings(params (string Key, string Value)[] settings) =>
        new GameApiFactory().WithWebHostBuilder(builder =>
        {
            foreach (var (key, value) in settings)
            {
                builder.UseSetting(key, value);
            }
        });

    private static async Task<HttpClient> SignedInClientAsync(WebApplicationFactory<Program> factory)
    {
        var (client, _) = await RegisterAsync(factory);
        return client;
    }

    private static async Task<(HttpClient Client, string Token)> RegisterAsync(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        var username = $"hero{Guid.NewGuid():N}"[..20];
        var response = await client.PostAsJsonAsync("/api/v1/auth/register", new { Username = username, Password });
        response.EnsureSuccessStatusCode();
        var auth = (await response.Content.ReadFromJsonAsync<AuthResponse>(Json))!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        return (client, auth.AccessToken);
    }
}
