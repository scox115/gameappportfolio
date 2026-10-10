using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Game.Api.Models;
using Game.Api.Options;
using Game.Core.Admin;
using Game.Core.Events;
using Game.Core.History;
using Game.Core.Moderation;
using Game.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Game.Api.Tests;

// Offensive names and portraits: the rules at sign-up, players' reports, and what admins do about them.
public class ModerationTests
{
    private const string Password = "Arena-Pass1";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    [Theory]
    [InlineData("Big_Sh1t")]
    [InlineData("Admin Bob")]
    [InlineData("hi")]
    [InlineData("<script>")]
    public async Task SignUp_RefusesNamesThatBreakTheRules(string name)
    {
        using var factory = new GameApiFactory();

        var response = await factory.CreateClient().PostAsJsonAsync("/api/v1/auth/register", new { Username = name, Password });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Username", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task AnAdminListedInSettings_CanSignUpWithAStaffName()
    {
        using var factory = new GameApiFactory();
        factory.Services.GetRequiredService<IOptions<AdminOptions>>().Value.Usernames = "RefereeMod";

        var admin = await SignUpAsync(factory, "RefereeMod");
        var impostor = await factory.CreateClient().PostAsJsonAsync("/api/v1/auth/register", new { Username = "RefereeMod2", Password });

        Assert.Equal("RefereeMod", admin.Username);
        Assert.Equal(HttpStatusCode.BadRequest, impostor.StatusCode);
    }

    [Fact]
    public async Task APlayerCanReportAnotherHero_AndTheReportWaitsInTheAdminQueue()
    {
        using var factory = new GameApiFactory();
        var admin = await AdminAsync(factory);
        var target = await SignUpAsync(factory);
        var alice = await SignUpAsync(factory);
        var bob = await SignUpAsync(factory);

        Assert.Equal(HttpStatusCode.Accepted, (await ReportAsync(alice, target, ReportReason.Name, "Means something rude")).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await ReportAsync(alice, target, ReportReason.Name, "Again")).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await ReportAsync(bob, target, ReportReason.Name)).StatusCode);

        var line = Assert.Single(await QueueAsync(admin));
        // A second click by the same player doesn't count twice.
        Assert.Equal((target.Id, target.Username, ReportReason.Name, 2), (line.TargetId, line.TargetName, line.Reason, line.Reports));
        Assert.Equal(["Means something rude"], line.Notes);

        Assert.Equal(HttpStatusCode.Forbidden, (await alice.Client.GetAsync("/api/v1/admin/reports")).StatusCode);
        var detail = await admin.Client.GetFromJsonAsync<AdminPlayerDetail>($"/api/v1/admin/players/{target.Id}", Json);
        Assert.Equal([new OpenReportCount(ReportReason.Name, 2)], detail!.OpenReports);
    }

    [Fact]
    public async Task Reports_ThatMakeNoSenseAreRefused()
    {
        using var factory = new GameApiFactory();
        var alice = await SignUpAsync(factory);
        var target = await SignUpAsync(factory);

        Assert.Equal(HttpStatusCode.BadRequest, (await ReportAsync(alice, alice, ReportReason.Name)).StatusCode);
        var noPortrait = await ReportAsync(alice, target, ReportReason.Portrait);
        Assert.Equal(HttpStatusCode.BadRequest, noPortrait.StatusCode);
        Assert.Contains("no portrait", await noPortrait.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, (await ReportAsync(alice, target, ReportReason.Name, new string('x', PlayerReport.NoteMaxLength + 1))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await alice.Client.PostAsJsonAsync($"/api/v1/players/{Guid.NewGuid()}/reports", new { Reason = "Name" }, Json)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().PostAsJsonAsync($"/api/v1/players/{target.Id}/reports", new { Reason = "Name" }, Json)).StatusCode);
    }

    [Fact]
    public async Task Reporting_IsRateLimitedPerPlayer()
    {
        using var factory = new GameApiFactory();
        factory.Services.GetRequiredService<IOptions<AntiCheatOptions>>().Value.ReportsPerHour = 2;
        var alice = await SignUpAsync(factory);
        var targets = new[] { await SignUpAsync(factory), await SignUpAsync(factory), await SignUpAsync(factory) };

        var statuses = new List<HttpStatusCode>();
        foreach (var target in targets) statuses.Add((await ReportAsync(alice, target, ReportReason.Name)).StatusCode);

        Assert.Equal([HttpStatusCode.Accepted, HttpStatusCode.Accepted, HttpStatusCode.TooManyRequests], statuses);
    }

    [Fact]
    public async Task RenamingAHero_ClosesTheReports_KeepsTheirSignIn_AndUpdatesHistory()
    {
        using var factory = new GameApiFactory();
        var admin = await AdminAsync(factory);
        var target = await SignUpAsync(factory);
        var reporter = await SignUpAsync(factory);
        await ReportAsync(reporter, target, ReportReason.Name);
        await RecordDuelAsync(factory, reporter, target);

        var renamed = await PostAsync<AdminPlayerDetail>(admin.Client, $"/api/v1/admin/players/{target.Id}/rename",
            new { NewName = "Gentle Knight", Reason = "Offensive name, reported by players" });

        Assert.Equal("Gentle Knight", renamed.Username);
        Assert.Empty(renamed.OpenReports!);
        Assert.Empty(await QueueAsync(admin));
        var entry = (await AuditAsync(admin.Client, AdminAction.Rename)).Single();
        Assert.Equal((target.Username, $"{target.Username} → Gentle Knight; 1 report closed."), (entry.TargetName, entry.Detail));

        // The opponent's history no longer shows the old name.
        var history = await reporter.Client.GetFromJsonAsync<List<MatchHistoryItemResponse>>("/api/v1/players/me/matches", Json);
        Assert.Equal("Gentle Knight", Assert.Single(history!).OpponentName);

        // The player can still sign in with either name, and is told the new one.
        foreach (var name in new[] { target.Username, "gentle knight" })
        {
            var signIn = await factory.CreateClient().PostAsJsonAsync("/api/v1/auth/login", new { Username = name, Password });
            Assert.Equal(HttpStatusCode.OK, signIn.StatusCode);
            Assert.Equal("Gentle Knight", (await signIn.Content.ReadFromJsonAsync<AuthResponse>(Json))!.Player.Username);
        }

        // Nobody else can take the old name.
        var taken = await factory.CreateClient().PostAsJsonAsync("/api/v1/auth/register", new { Username = target.Username, Password });
        Assert.Equal(HttpStatusCode.Conflict, taken.StatusCode);
    }

    [Fact]
    public async Task ARename_FollowsTheNameRules_AndCantTakeAnotherHerosName()
    {
        using var factory = new GameApiFactory();
        var admin = await AdminAsync(factory);
        var target = await SignUpAsync(factory);
        var other = await SignUpAsync(factory);

        var rude = await admin.Client.PostAsJsonAsync($"/api/v1/admin/players/{target.Id}/rename", new { NewName = "Fuuuck", Reason = "Test" });
        var taken = await admin.Client.PostAsJsonAsync($"/api/v1/admin/players/{target.Id}/rename", new { NewName = other.Username.ToUpperInvariant(), Reason = "Test" });
        var noReason = await admin.Client.PostAsJsonAsync($"/api/v1/admin/players/{target.Id}/rename", new { NewName = "Fine Name", Reason = " " });

        Assert.Equal(HttpStatusCode.BadRequest, rude.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, taken.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, noReason.StatusCode);
        Assert.Empty(await AuditAsync(admin.Client, AdminAction.Rename));
    }

    [Fact]
    public async Task RemovingAPortrait_DeletesTheImage_AndClosesPortraitReportsOnly()
    {
        using var factory = new GameApiFactory();
        var admin = await AdminAsync(factory);
        var target = await SignUpAsync(factory);
        var reporter = await SignUpAsync(factory);
        const string portrait = "https://blob.test/player-avatars/rude.png";
        await SetPortraitAsync(factory, target.Id, portrait);
        await ReportAsync(reporter, target, ReportReason.Portrait, "Not suitable");
        await ReportAsync(reporter, target, ReportReason.Name);

        var queue = await QueueAsync(admin);
        Assert.Equal(portrait, queue.Single(q => q.Reason == ReportReason.Portrait).AvatarUrl);
        Assert.Null(queue.Single(q => q.Reason == ReportReason.Name).AvatarUrl);

        var after = await PostAsync<AdminPlayerDetail>(admin.Client, $"/api/v1/admin/players/{target.Id}/remove-portrait", new { Reason = "Offensive portrait" });

        Assert.Null(after.AvatarUrl);
        Assert.Equal([new OpenReportCount(ReportReason.Name, 1)], after.OpenReports);
        Assert.Contains(portrait, factory.Storage.Deleted);
        var again = await admin.Client.PostAsJsonAsync($"/api/v1/admin/players/{target.Id}/remove-portrait", new { Reason = "Twice" });
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
    }

    [Fact]
    public async Task DismissingReports_ClosesThemWithNothingChanged()
    {
        using var factory = new GameApiFactory();
        var admin = await AdminAsync(factory);
        var target = await SignUpAsync(factory);
        var reporter = await SignUpAsync(factory);
        await ReportAsync(reporter, target, ReportReason.Name, "I just don't like it");

        var after = await PostAsync<AdminPlayerDetail>(admin.Client, $"/api/v1/admin/players/{target.Id}/dismiss-reports",
            new { Kind = "Name", Reason = "The name is fine" });

        Assert.Equal(target.Username, after.Username);
        Assert.Empty(await QueueAsync(admin));
        Assert.Equal("1 name report closed.", (await AuditAsync(admin.Client, AdminAction.DismissReports)).Single().Detail);
        var nothingLeft = await admin.Client.PostAsJsonAsync($"/api/v1/admin/players/{target.Id}/dismiss-reports", new { Kind = "Name", Reason = "Again" });
        Assert.Equal(HttpStatusCode.Conflict, nothingLeft.StatusCode);

        // The reporter sees what came of it in their data export.
        var export = await reporter.Client.GetFromJsonAsync<JsonElement>("/api/v1/players/me/export", Json);
        var filed = export.GetProperty("reportsFiled").EnumerateArray().Single();
        Assert.Equal("Dismissed", filed.GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task DeletingAnAccount_RemovesReportsByAndAboutTheHero()
    {
        using var factory = new GameApiFactory();
        var admin = await AdminAsync(factory);
        var target = await SignUpAsync(factory);
        var reporter = await SignUpAsync(factory);
        await ReportAsync(reporter, target, ReportReason.Name);
        await ReportAsync(target, reporter, ReportReason.Name);

        var deleted = await target.Client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, "/api/v1/players/me")
        {
            Content = JsonContent.Create(new { Password })
        });

        Assert.True(deleted.IsSuccessStatusCode, await deleted.Content.ReadAsStringAsync());
        Assert.Empty(await QueueAsync(admin));
        using var scope = factory.Services.CreateScope();
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<AppDbContext>().PlayerReports.ToListAsync());
    }

    private record Hero(HttpClient Client, Guid Id, string Username);

    private static string NewName(string prefix) => $"{prefix}{Guid.NewGuid():N}"[..16];

    private static async Task<Hero> AdminAsync(GameApiFactory factory)
    {
        var name = NewName("boss");
        factory.Services.GetRequiredService<IOptions<AdminOptions>>().Value.Usernames = name;
        var admin = await SignUpAsync(factory, name);
        await factory.TurnOnTwoFactorAsync(admin.Id);
        return admin;
    }

    private static async Task<Hero> SignUpAsync(GameApiFactory factory, string? username = null)
    {
        var response = await factory.CreateClient().PostAsJsonAsync("/api/v1/auth/register", new { Username = username ?? NewName("hero"), Password });
        response.EnsureSuccessStatusCode();
        var auth = (await response.Content.ReadFromJsonAsync<AuthResponse>(Json))!;
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        return new Hero(client, auth.Player.Id, auth.Player.Username);
    }

    private static Task<HttpResponseMessage> ReportAsync(Hero reporter, Hero target, ReportReason reason, string? note = null) =>
        reporter.Client.PostAsJsonAsync($"/api/v1/players/{target.Id}/reports", new { Reason = reason, Note = note }, Json);

    private static async Task<List<ReportQueueItem>> QueueAsync(Hero admin) =>
        (await admin.Client.GetFromJsonAsync<List<ReportQueueItem>>("/api/v1/admin/reports", Json))!;

    private static async Task<T> PostAsync<T>(HttpClient client, string url, object body)
    {
        var response = await client.PostAsJsonAsync(url, body, Json);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<T>(Json))!;
    }

    private static async Task<List<AuditEntryResponse>> AuditAsync(HttpClient client, AdminAction of) =>
        (await client.GetFromJsonAsync<List<AuditEntryResponse>>("/api/v1/admin/audit?take=50", Json))!.Where(e => e.Action == of).ToList();

    private static async Task SetPortraitAsync(GameApiFactory factory, Guid playerId, string url)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        (await db.Players.FindAsync(playerId))!.UpdateAvatar(url);
        await db.SaveChangesAsync();
    }

    // A finished duel in the opponent's match history, as the consumer would record it.
    private static async Task RecordDuelAsync(GameApiFactory factory, Hero viewer, Hero opponent)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var match = new MatchCompletedEvent(Guid.NewGuid(), viewer.Id, opponent.Id)
        {
            OccurredAt = DateTime.UtcNow,
            Kind = MatchKind.Duel,
            EndReason = Core.Battles.PvpEndReason.Forfeit,
            Turns = 3,
            Participants =
            [
                new MatchParticipant(viewer.Id, viewer.Username, Core.Battles.HeroClass.Paladin, true, 10, 5),
                new MatchParticipant(opponent.Id, opponent.Username, Core.Battles.HeroClass.Ranger, false, 2, 1)
            ]
        };
        db.MatchHistory.AddRange(MatchHistoryEntry.From(match));
        await db.SaveChangesAsync();
    }
}
