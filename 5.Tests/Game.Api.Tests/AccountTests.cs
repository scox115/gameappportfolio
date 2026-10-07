using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Game.Api.Models;
using Game.Core.Battles;
using Game.Core.Entities;
using Game.Core.Events;
using Game.Core.History;
using Game.Infrastructure.Data;
using Game.Infrastructure.History;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Game.Api.Tests;

// "Download my data" and "Delete my hero".
public class AccountTests
{
    private const string Password = "Arena-Pass1";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    // A 1x1 PNG.
    private static readonly byte[] Portrait = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");

    [Fact]
    public async Task TheExport_HoldsTheHeroAndTheirHistory_ButNoSecrets()
    {
        using var factory = new GameApiFactory();
        var alice = await SignUpAsync(factory, "alice");
        var bob = await SignUpAsync(factory, "bob");
        await StartBossFightAsync(alice.Client);
        await ProjectAsync(factory, Duel(alice, bob));

        var response = await alice.Client.GetAsync("/api/v1/players/me/export");

        response.EnsureSuccessStatusCode();
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition?.DispositionType);
        Assert.StartsWith($"card-arena-{alice.Username}-", response.Content.Headers.ContentDisposition?.FileNameStar);

        var text = await response.Content.ReadAsStringAsync();
        var export = JsonDocument.Parse(text).RootElement;
        Assert.Equal(alice.Username, export.GetProperty("account").GetProperty("username").GetString());
        Assert.Equal(alice.Id, export.GetProperty("account").GetProperty("id").GetGuid());
        Assert.Equal("Sorcerer", export.GetProperty("profile").GetProperty("class").GetString());
        Assert.Equal(1, export.GetProperty("signInSessions").GetArrayLength());
        Assert.Equal(1, export.GetProperty("bossFights").GetArrayLength());
        var match = export.GetProperty("matchHistory").EnumerateArray().Single();
        Assert.Equal((bob.Username, true), (match.GetProperty("opponent").GetString(), match.GetProperty("won").GetBoolean()));

        // Nothing that would help someone sign in as them, or that's only the server's business.
        foreach (var secret in new[] { "passwordHash", "tokenHash", "securityStamp", "concurrencyStamp", alice.RefreshToken })
        {
            Assert.DoesNotContain(secret, text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task TheExportAndDelete_NeedASignedInHero()
    {
        using var factory = new GameApiFactory();
        var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/players/me/export")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await DeleteAsync(client, Password)).StatusCode);
    }

    [Fact]
    public async Task Deleting_WithTheWrongPassword_KeepsEverything()
    {
        using var factory = new GameApiFactory();
        var alice = await SignUpAsync(factory, "alice");

        var response = await DeleteAsync(alice.Client, "not-my-password");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await alice.Client.GetAsync("/api/v1/players/me")).StatusCode);
    }

    [Fact]
    public async Task Deleting_RemovesTheHeroAndEverythingRecordedAboutThem()
    {
        using var factory = new GameApiFactory();
        var alice = await SignUpAsync(factory, "alice");
        var bob = await SignUpAsync(factory, "bob");
        var portrait = await UploadPortraitAsync(alice.Client);
        await StartBossFightAsync(alice.Client);
        await ProjectAsync(factory, Duel(alice, bob));
        await AddFinishedDuelAsync(factory, alice.Id, bob.Id);

        var response = await DeleteAsync(alice.Client, Password);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.False(await db.Players.AnyAsync(p => p.Id == alice.Id));
            Assert.False(await db.Users.AnyAsync(u => u.Id == alice.Id));
            Assert.False(await db.RefreshTokens.AnyAsync(t => t.UserId == alice.Id));
            Assert.False(await db.PveBattles.AnyAsync(b => b.PlayerId == alice.Id));
            Assert.False(await db.PvpBattles.AnyAsync(b => b.PlayerOneId == alice.Id || b.PlayerTwoId == alice.Id));
            Assert.False(await db.Matches.AnyAsync(m => m.PlayerOneId == alice.Id || m.PlayerTwoId == alice.Id));
            Assert.False(await db.MatchHistory.AnyAsync(e => e.PlayerId == alice.Id));

            // Bob keeps his record of the duel, but not her name; the day's totals still count it.
            var bobsDuel = await db.MatchHistory.SingleAsync(e => e.PlayerId == bob.Id);
            Assert.Equal(MatchHistoryEntry.RetiredHeroName, bobsDuel.OpponentName);
            Assert.Equal(1, (await db.DailyArenaStats.SingleAsync()).Duels);
            Assert.True(await db.Players.AnyAsync(p => p.Id == bob.Id));
        }

        Assert.Contains(portrait, factory.Storage.Deleted);

        // Her tokens stop working at once, and the name is free for someone new.
        Assert.Equal(HttpStatusCode.Unauthorized, (await alice.Client.GetAsync("/api/v1/players/me")).StatusCode);
        var refresh = await factory.CreateClient().PostAsJsonAsync("/api/v1/auth/refresh", new { alice.RefreshToken });
        Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);
        var signIn = await factory.CreateClient().PostAsJsonAsync("/api/v1/auth/login", new { alice.Username, Password });
        Assert.Equal(HttpStatusCode.Unauthorized, signIn.StatusCode);
        var again = await factory.CreateClient().PostAsJsonAsync("/api/v1/auth/register", new { alice.Username, Password });
        Assert.Equal(HttpStatusCode.Created, again.StatusCode);
    }

    [Fact]
    public async Task Deleting_DuringADuel_IsRefused()
    {
        using var factory = new GameApiFactory();
        var alice = await SignUpAsync(factory, "alice");
        var bob = await SignUpAsync(factory, "bob");
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.PvpBattles.Add(PvpBattle.Start(bob.Id, alice.Id, factory.Clock.GetUtcNow().UtcDateTime));
            await db.SaveChangesAsync();
        }

        var response = await DeleteAsync(alice.Client, Password);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await alice.Client.GetAsync("/api/v1/players/me")).StatusCode);
    }

    [Fact]
    public async Task ABossFightThatArrivesAfterTheDelete_IsNotRecorded()
    {
        using var factory = new GameApiFactory();
        var alice = await SignUpAsync(factory, "alice");
        var fight = new MatchCompletedEvent(Guid.NewGuid(), alice.Id, GameMatch.AiBossId)
        {
            OccurredAt = DateTime.UtcNow,
            Kind = MatchKind.Boss,
            Difficulty = BossDifficulty.Normal,
            Turns = 5,
            Participants = [new MatchParticipant(alice.Id, alice.Username, HeroClass.Sorcerer, true, 100, 50, null, 0)]
        };
        (await DeleteAsync(alice.Client, Password)).EnsureSuccessStatusCode();

        Assert.Equal(ProjectionResult.HeroesDeleted, await ProjectAsync(factory, fight));
        Assert.Equal(ProjectionResult.HeroesDeleted, await ProjectAsync(factory, fight));

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Empty(db.MatchHistory);
        Assert.Empty(db.DailyArenaStats);
    }

    [Fact]
    public async Task AMatchThatArrivesAfterTheDelete_IsRecordedOnlyForTheOpponent_WithoutTheName()
    {
        using var factory = new GameApiFactory();
        var alice = await SignUpAsync(factory, "alice");
        var bob = await SignUpAsync(factory, "bob");
        var duel = Duel(alice, bob);
        (await DeleteAsync(alice.Client, Password)).EnsureSuccessStatusCode();

        Assert.Equal(ProjectionResult.Recorded, await ProjectAsync(factory, duel));

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var entry = await db.MatchHistory.SingleAsync();
        Assert.Equal((bob.Id, MatchHistoryEntry.RetiredHeroName), (entry.PlayerId, entry.OpponentName));
    }

    private record Hero(HttpClient Client, Guid Id, string Username, string RefreshToken);

    private static async Task<Hero> SignUpAsync(GameApiFactory factory, string prefix)
    {
        var client = factory.CreateClient();
        var username = $"{prefix}{Guid.NewGuid():N}"[..16];
        var response = await client.PostAsJsonAsync("/api/v1/auth/register", new { Username = username, Password });
        response.EnsureSuccessStatusCode();
        var auth = (await response.Content.ReadFromJsonAsync<AuthResponse>(Json))!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        return new Hero(client, auth.Player.Id, username, auth.RefreshToken);
    }

    private static Task<HttpResponseMessage> DeleteAsync(HttpClient client, string password) =>
        client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, "/api/v1/players/me")
        {
            Content = JsonContent.Create(new { Password = password })
        });

    private static async Task StartBossFightAsync(HttpClient client) =>
        (await client.PostAsync("/api/v1/battles/pve", null)).EnsureSuccessStatusCode();

    private static async Task<string> UploadPortraitAsync(HttpClient client)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(Portrait);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(file, "File", "me.png");
        var response = await client.PostAsync("/api/v1/players/me/avatar", form);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("avatarUrl").GetString()!;
    }

    private static async Task AddFinishedDuelAsync(GameApiFactory factory, Guid first, Guid second)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var duel = PvpBattle.Start(first, second, factory.Clock.GetUtcNow().UtcDateTime);
        duel.Forfeit(second, factory.Clock.GetUtcNow().UtcDateTime);
        db.PvpBattles.Add(duel);
        await db.SaveChangesAsync();
    }

    private static MatchCompletedEvent Duel(Hero winner, Hero loser) =>
        new(Guid.NewGuid(), winner.Id, loser.Id)
        {
            OccurredAt = DateTime.UtcNow,
            Kind = MatchKind.Duel,
            EndReason = PvpEndReason.Forfeit,
            Turns = 4,
            Participants =
            [
                new MatchParticipant(winner.Id, winner.Username, HeroClass.Sorcerer, true, 100, 50, 14, 0),
                new MatchParticipant(loser.Id, loser.Username, HeroClass.Sorcerer, false, 20, 10, -14, 0)
            ]
        };

    private static async Task<ProjectionResult> ProjectAsync(GameApiFactory factory, MatchCompletedEvent match)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<MatchHistoryProjector>().ProjectAsync(match);
    }
}
