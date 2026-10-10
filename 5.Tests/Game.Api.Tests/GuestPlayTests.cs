using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Game.Api.Models;
using Game.Api.Recovery;
using Game.Api.Workers;
using Game.Core.Battles;
using Game.Infrastructure.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Game.Api.Tests;

// Playing as a guest, keeping the hero, and cleaning up the guests nobody kept (docs/adr/0030-guest-play.md).
public class GuestPlayTests
{
    private const string Password = "Arena-Pass1";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    [Fact]
    public async Task AGuest_StartsWithAMadeUpNameAndASession()
    {
        using var factory = new GameApiFactory();

        var guest = await StartGuestAsync(factory, HeroClass.Paladin);

        Assert.Matches("^Guest[0-9]{6}$", guest.Username);
        var me = await guest.Client.GetFromJsonAsync<PlayerProfileResponse>("/api/v1/players/me", Json);
        Assert.True(me!.IsGuest);
        Assert.Equal(HeroClass.Paladin, me.Class);
    }

    [Fact]
    public async Task AGuest_CantSignInWithAPassword()
    {
        using var factory = new GameApiFactory();
        var guest = await StartGuestAsync(factory);

        var response = await factory.CreateClient().PostAsJsonAsync("/api/v1/auth/login", new { guest.Username, Password = "" });

        Assert.False(response.IsSuccessStatusCode);
    }

    [Fact]
    public async Task Guests_AreLeftOffTheLeaderboards()
    {
        using var factory = new GameApiFactory();
        var hero = await SignUpAsync(factory, "kept");
        var guest = await StartGuestAsync(factory);

        var names = await LeaderboardNamesAsync(factory.CreateClient(), "/api/v1/players/leaderboard");

        Assert.Contains(hero, names);
        Assert.DoesNotContain(guest.Username, names);
    }

    [Fact]
    public async Task KeepingAGuest_GivesItTheChosenNameAndAPassword()
    {
        using var factory = new GameApiFactory();
        var guest = await StartGuestAsync(factory);
        var name = $"kept{Guid.NewGuid():N}"[..16];

        var response = await guest.Client.PostAsJsonAsync("/api/v1/players/me/keep", new { Username = $" {name} ", Password }, Json);

        response.EnsureSuccessStatusCode();
        var kept = (await response.Content.ReadFromJsonAsync<PlayerProfileResponse>(Json))!;
        Assert.Equal((guest.Id, name, false), (kept.Id, kept.Username, kept.IsGuest));

        var login = await factory.CreateClient().PostAsJsonAsync("/api/v1/auth/login", new { Username = name, Password });
        login.EnsureSuccessStatusCode();
        Assert.Contains(name, await LeaderboardNamesAsync(factory.CreateClient(), "/api/v1/players/leaderboard"));
    }

    [Fact]
    public async Task KeepingAGuest_WithAnEmail_SendsAConfirmationLink_AndABadOneChangesNothing()
    {
        using var factory = new GameApiFactory();
        var guest = await StartGuestAsync(factory);
        var name = $"kept{Guid.NewGuid():N}"[..16];

        var badAddress = await guest.Client.PostAsJsonAsync("/api/v1/players/me/keep",
            new { Username = name, Password, RecoveryEmail = "kept at example" }, Json);
        Assert.Equal(HttpStatusCode.BadRequest, badAddress.StatusCode);
        Assert.Contains("recoveryEmail", await badAddress.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        Assert.True((await guest.Client.GetFromJsonAsync<PlayerProfileResponse>("/api/v1/players/me", Json))!.IsGuest);

        var kept = await guest.Client.PostAsJsonAsync("/api/v1/players/me/keep",
            new { Username = name, Password, RecoveryEmail = " kept@example.com " }, Json);

        kept.EnsureSuccessStatusCode();
        var email = Assert.Single(factory.Email.To("kept@example.com"));
        Assert.Contains(name, email.Subject + email.PlainText);
        Assert.Equal(new RecoveryEmailResponse(null, "kept@example.com"),
            await guest.Client.GetFromJsonAsync<RecoveryEmailResponse>("/api/v1/players/me/recovery-email", Json));
    }

    [Fact]
    public async Task KeepingAGuest_UnderATakenName_IsRefused()
    {
        using var factory = new GameApiFactory();
        var taken = await SignUpAsync(factory, "taken");
        var guest = await StartGuestAsync(factory);

        var response = await guest.Client.PostAsJsonAsync("/api/v1/players/me/keep", new { Username = taken.ToUpperInvariant(), Password }, Json);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.True((await guest.Client.GetFromJsonAsync<PlayerProfileResponse>("/api/v1/players/me", Json))!.IsGuest);
    }

    [Fact]
    public async Task KeepingAGuest_WithAWeakPassword_ChangesNothing()
    {
        using var factory = new GameApiFactory();
        var guest = await StartGuestAsync(factory);
        var name = $"weak{Guid.NewGuid():N}"[..16];

        var response = await guest.Client.PostAsJsonAsync("/api/v1/players/me/keep", new { Username = name, Password = "short" }, Json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.True(problem.GetProperty("errors").TryGetProperty("Password", out _));
        var me = (await guest.Client.GetFromJsonAsync<PlayerProfileResponse>("/api/v1/players/me", Json))!;
        Assert.Equal((guest.Username, true), (me.Username, me.IsGuest));
    }

    [Fact]
    public async Task AHeroThatWasSignedUpFor_HasNothingToKeep()
    {
        using var factory = new GameApiFactory();
        var client = factory.CreateClient();
        var username = $"hero{Guid.NewGuid():N}"[..16];
        var auth = await (await client.PostAsJsonAsync("/api/v1/auth/register", new { Username = username, Password }))
            .Content.ReadFromJsonAsync<AuthResponse>(Json);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth!.AccessToken);

        var response = await client.PostAsJsonAsync("/api/v1/players/me/keep", new { Username = "another" + username[4..], Password }, Json);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.False(auth.Player.IsGuest);
    }

    [Fact]
    public async Task TooManyGuestsFromOneAddress_AreTurnedAway()
    {
        using var factory = new GameApiFactory();
        using var strict = factory.WithWebHostBuilder(builder => builder.UseSetting("AntiCheat:GuestsPerHour", "2"));
        var client = strict.CreateClient();

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 3; i++)
        {
            statuses.Add((await client.PostAsJsonAsync("/api/v1/auth/guest", new { })).StatusCode);
        }

        Assert.Equal([HttpStatusCode.Created, HttpStatusCode.Created, HttpStatusCode.TooManyRequests], statuses);
    }

    [Fact]
    public async Task Cleanup_DeletesGuestsNobodyCanPlayAnyMore_AndKeepsTheRest()
    {
        using var factory = new GameApiFactory();
        var abandoned = await StartGuestAsync(factory);
        var kept = await StartGuestAsync(factory);
        (await kept.Client.PostAsJsonAsync("/api/v1/players/me/keep", new { Username = $"kept{Guid.NewGuid():N}"[..16], Password }, Json))
            .EnsureSuccessStatusCode();

        // A week and a bit later every session from the first day has run out...
        factory.Clock.Advance(TimeSpan.FromDays(8));
        // ...but a guest started today is still being played.
        var playing = await StartGuestAsync(factory);
        factory.Clock.Advance(TimeSpan.FromDays(2));

        using var scope = factory.Services.CreateScope();
        var deleted = await scope.ServiceProvider.GetRequiredService<GuestCleanupService>().RunAsync();

        Assert.Equal(1, deleted);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var left = await db.Players.AsNoTracking().Select(p => p.Id).ToListAsync();
        Assert.DoesNotContain(abandoned.Id, left);
        Assert.Contains(kept.Id, left);
        Assert.Contains(playing.Id, left);
        Assert.False(await db.Users.AnyAsync(u => u.Id == abandoned.Id));
    }

    private static async Task<Guest> StartGuestAsync(GameApiFactory factory, HeroClass? heroClass = null)
    {
        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/v1/auth/guest", new { Class = heroClass }, Json);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var auth = (await response.Content.ReadFromJsonAsync<AuthResponse>(Json))!;
        Assert.True(auth.Player.IsGuest);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        return new Guest(client, auth.Player.Id, auth.Player.Username);
    }

    private static async Task<string> SignUpAsync(GameApiFactory factory, string prefix)
    {
        var username = $"{prefix}{Guid.NewGuid():N}"[..16];
        (await factory.CreateClient().PostAsJsonAsync("/api/v1/auth/register", new { Username = username, Password }))
            .EnsureSuccessStatusCode();
        return username;
    }

    private static async Task<List<string>> LeaderboardNamesAsync(HttpClient client, string path)
    {
        var rows = JsonDocument.Parse(await client.GetStringAsync(path)).RootElement;
        return rows.EnumerateArray().Select(r => r.GetProperty("username").GetString()!).ToList();
    }

    private sealed record Guest(HttpClient Client, Guid Id, string Username);
}
