using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Game.Api.Auth;
using Game.Api.Models;
using Game.Api.Options;
using Game.Core.Admin;
using Game.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Game.Api.Tests;

// Admin tools: who is an admin, what they can do to an account, and the audit log that records it.
public class AdminTests
{
    private const string Password = "Arena-Pass1";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    [Fact]
    public async Task AdminTools_AreOnlyForAdmins()
    {
        using var factory = new GameApiFactory();
        var alice = await SignUpAsync(factory, "alice");

        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync("/api/v1/admin/players")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await alice.Client.GetAsync("/api/v1/admin/players")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await alice.Client.GetAsync("/api/v1/admin/audit")).StatusCode);
        var selfHelp = await alice.Client.PostAsJsonAsync($"/api/v1/admin/players/{alice.Id}/gold", new { Change = 1000, Reason = "Please" });
        Assert.Equal(HttpStatusCode.Forbidden, selfHelp.StatusCode);
        Assert.Empty(alice.Roles);
    }

    [Fact]
    public async Task AnAccountListedInTheSettings_BecomesAnAdminWhenItSignsIn()
    {
        using var factory = new GameApiFactory();
        var name = NewName("boss");
        SetAdmins(factory, $"someone-else, {name.ToUpperInvariant()}");

        var boss = await SignUpAsync(factory, username: name);

        Assert.Equal([GameRoles.Admin], boss.Roles);
        var token = new JwtSecurityTokenHandler().ReadJwtToken(boss.AccessToken);
        Assert.Equal(GameRoles.Admin, token.Claims.Single(c => c.Type == TokenService.RoleClaim).Value);
        Assert.Equal(HttpStatusCode.OK, (await boss.Client.GetAsync("/api/v1/admin/players")).StatusCode);

        var grant = (await AuditAsync(boss.Client)).Single();
        Assert.Equal((AdminAction.GrantAdmin, AuditLogEntry.ConfigurationActor, boss.Id), (grant.Action, grant.ActorName, grant.TargetId));
    }

    [Fact]
    public async Task TakingANameOutOfTheSettings_RemovesTheRoleAtTheNextSignIn()
    {
        using var factory = new GameApiFactory();
        var name = NewName("boss");
        SetAdmins(factory, name);
        await SignUpAsync(factory, username: name);

        SetAdmins(factory, "");
        var again = await SignInAsync(factory, name);

        Assert.Empty(again.Roles);
        Assert.Equal(HttpStatusCode.Forbidden, (await again.Client.GetAsync("/api/v1/admin/players")).StatusCode);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(
            [AdminAction.GrantAdmin, AdminAction.RevokeAdmin],
            await db.AuditLog.OrderBy(e => e.Id).Select(e => e.Action).ToListAsync());
    }

    [Fact]
    public async Task AdminsCanFindPlayersByPartOfTheirName()
    {
        using var factory = new GameApiFactory();
        var boss = await AdminAsync(factory);
        var alice = await SignUpAsync(factory, "alice");
        await SignUpAsync(factory, "bob");

        var found = await boss.Client.GetFromJsonAsync<List<AdminPlayerSummary>>($"/api/v1/admin/players?search={alice.Username[2..8]}", Json);

        var match = Assert.Single(found!);
        Assert.Equal((alice.Id, alice.Username, 200, false), (match.Id, match.Username, match.Gold, match.IsAdmin));
        var everyone = await boss.Client.GetFromJsonAsync<List<AdminPlayerSummary>>("/api/v1/admin/players", Json);
        Assert.Equal(3, everyone!.Count);
        Assert.True(everyone.Single(p => p.Id == boss.Id).IsAdmin);
        Assert.Equal(HttpStatusCode.NotFound, (await boss.Client.GetAsync($"/api/v1/admin/players/{Guid.NewGuid()}")).StatusCode);
    }

    [Fact]
    public async Task ASuspension_SignsThePlayerOutAndKeepsThemOut_UntilTheyAreReinstated()
    {
        using var factory = new GameApiFactory();
        var boss = await AdminAsync(factory);
        var alice = await SignUpAsync(factory, "alice");

        var suspended = await PostAsync<AdminPlayerDetail>(boss.Client, $"/api/v1/admin/players/{alice.Id}/suspend",
            new { Reason = "Used a bot in duels." });

        Assert.Equal(new SuspensionResponse(null, "Used a bot in duels."), suspended.Suspension);
        var entry = Assert.Single(suspended.History);
        Assert.Equal((AdminAction.Suspend, boss.Username, "Until reinstated."), (entry.Action, entry.ActorName, entry.Detail));

        // Her browser is told why at once, and can't renew its session.
        var me = await alice.Client.GetAsync("/api/v1/players/me");
        Assert.Equal(HttpStatusCode.Unauthorized, me.StatusCode);
        Assert.Equal(SessionClaims.Suspended, me.Headers.GetValues(SessionClaims.EndedHeader).Single());
        var refresh = await factory.CreateClient().PostAsJsonAsync("/api/v1/auth/refresh", new { alice.RefreshToken });
        Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);
        Assert.Equal(SessionClaims.Suspended, refresh.Headers.GetValues(SessionClaims.EndedHeader).Single());

        // Signing in says why, but only once the password is right.
        var wrongPassword = await factory.CreateClient().PostAsJsonAsync("/api/v1/auth/login", new { alice.Username, Password = "guess-guess" });
        Assert.Equal(HttpStatusCode.Unauthorized, wrongPassword.StatusCode);
        var signIn = await factory.CreateClient().PostAsJsonAsync("/api/v1/auth/login", new { alice.Username, Password });
        Assert.Equal(HttpStatusCode.Forbidden, signIn.StatusCode);
        var problem = await signIn.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("This hero is suspended until an admin reinstates it. Reason: Used a bot in duels.", problem.GetProperty("detail").GetString());

        var reinstated = await PostAsync<AdminPlayerDetail>(boss.Client, $"/api/v1/admin/players/{alice.Id}/reinstate",
            new { Reason = "Appeal accepted: it was her brother." });

        Assert.Null(reinstated.Suspension);
        Assert.Equal([AdminAction.Reinstate, AdminAction.Suspend], reinstated.History.Select(e => e.Action));
        Assert.Equal(HttpStatusCode.OK, (await SignInResponseAsync(factory, alice.Username)).StatusCode);
    }

    [Fact]
    public async Task ATimedSuspension_EndsOnItsOwn()
    {
        using var factory = new GameApiFactory();
        var boss = await AdminAsync(factory);
        var alice = await SignUpAsync(factory, "alice");

        var suspended = await PostAsync<AdminPlayerDetail>(boss.Client, $"/api/v1/admin/players/{alice.Id}/suspend",
            new { Reason = "Abusive name.", Days = 1 });

        Assert.Equal(factory.Clock.GetUtcNow().AddDays(1), suspended.Suspension!.Until!.Value, TimeSpan.FromSeconds(5));
        Assert.StartsWith("For 1 day, until ", suspended.History.Single().Detail);
        Assert.Equal(HttpStatusCode.Forbidden, (await SignInResponseAsync(factory, alice.Username)).StatusCode);

        factory.Clock.Advance(TimeSpan.FromHours(25));

        Assert.Equal(HttpStatusCode.OK, (await SignInResponseAsync(factory, alice.Username)).StatusCode);
    }

    [Theory]
    [InlineData("", null, "Give a reason")]
    [InlineData("Spam.", 0, "A suspension lasts from 1 to 3650 days")]
    [InlineData("Spam.", 3651, "A suspension lasts from 1 to 3650 days")]
    public async Task ASuspension_NeedsAReasonAndASensibleLength(string reason, int? days, string error)
    {
        using var factory = new GameApiFactory();
        var boss = await AdminAsync(factory);
        var alice = await SignUpAsync(factory, "alice");

        var response = await boss.Client.PostAsJsonAsync($"/api/v1/admin/players/{alice.Id}/suspend", new { Reason = reason, Days = days });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.StartsWith(error, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString());
        Assert.Empty(await AuditAsync(boss.Client, of: AdminAction.Suspend));
    }

    [Fact]
    public async Task AdminsCantSuspendThemselvesOrEachOther()
    {
        using var factory = new GameApiFactory();
        var first = NewName("boss");
        var second = NewName("boss");
        SetAdmins(factory, $"{first},{second}");
        var boss = await SignUpAsync(factory, username: first);
        var other = await SignUpAsync(factory, username: second);

        var self = await boss.Client.PostAsJsonAsync($"/api/v1/admin/players/{boss.Id}/suspend", new { Reason = "Testing." });
        var colleague = await boss.Client.PostAsJsonAsync($"/api/v1/admin/players/{other.Id}/suspend", new { Reason = "Testing." });
        var notSuspended = await boss.Client.PostAsJsonAsync($"/api/v1/admin/players/{other.Id}/reinstate", new { Reason = "Testing." });

        Assert.Equal(HttpStatusCode.BadRequest, self.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, colleague.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, notSuspended.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await other.Client.GetAsync("/api/v1/players/me")).StatusCode);
    }

    [Fact]
    public async Task AdminsCanCorrectGold_AndEveryCorrectionIsLogged()
    {
        using var factory = new GameApiFactory();
        var boss = await AdminAsync(factory);
        var alice = await SignUpAsync(factory, "alice");

        var refunded = await PostAsync<AdminPlayerDetail>(boss.Client, $"/api/v1/admin/players/{alice.Id}/gold",
            new { Change = 1500, Reason = "Refund: the shop charged twice." });
        var clawedBack = await PostAsync<AdminPlayerDetail>(boss.Client, $"/api/v1/admin/players/{alice.Id}/gold",
            new { Change = -700, Reason = "Undo a duplicated bounty reward." });
        var tooMuch = await boss.Client.PostAsJsonAsync($"/api/v1/admin/players/{alice.Id}/gold", new { Change = -5000, Reason = "Oops." });
        var noReason = await boss.Client.PostAsJsonAsync($"/api/v1/admin/players/{alice.Id}/gold", new { Change = 10, Reason = " " });

        Assert.Equal(1700, refunded.Gold);
        Assert.Equal(1000, clawedBack.Gold);
        Assert.Equal(HttpStatusCode.BadRequest, tooMuch.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, noReason.StatusCode);
        Assert.Equal(1000, (await alice.Client.GetFromJsonAsync<PlayerProfileResponse>("/api/v1/players/me", Json))!.Gold);

        var log = await AuditAsync(boss.Client, of: AdminAction.AdjustGold);
        Assert.Equal(["-700 gold (1,700 → 1,000).", "+1,500 gold (200 → 1,700)."], log.Select(e => e.Detail));
        Assert.All(log, e => Assert.Equal((boss.Username, alice.Id, alice.Username), (e.ActorName, e.TargetId, e.TargetName)));
    }

    [Fact]
    public async Task TheAuditLog_ReadsNewestFirst_InPages()
    {
        using var factory = new GameApiFactory();
        var boss = await AdminAsync(factory);
        var alice = await SignUpAsync(factory, "alice");
        for (var i = 1; i <= 5; i++)
        {
            await PostAsync<AdminPlayerDetail>(boss.Client, $"/api/v1/admin/players/{alice.Id}/gold", new { Change = i, Reason = $"Correction {i}" });
        }

        var first = await boss.Client.GetFromJsonAsync<List<AuditEntryResponse>>("/api/v1/admin/audit?take=3", Json);
        var next = await boss.Client.GetFromJsonAsync<List<AuditEntryResponse>>($"/api/v1/admin/audit?take=3&before={first!.Last().Id}", Json);

        Assert.Equal(["Correction 5", "Correction 4", "Correction 3"], first.Select(e => e.Reason));
        Assert.Equal(["Correction 2", "Correction 1", "Listed in Admin:Usernames."], next!.Select(e => e.Reason));
    }

    [Fact]
    public async Task APlayersExport_IncludesWhatAdminsDidToTheirAccount()
    {
        using var factory = new GameApiFactory();
        var boss = await AdminAsync(factory);
        var alice = await SignUpAsync(factory, "alice");
        await PostAsync<AdminPlayerDetail>(boss.Client, $"/api/v1/admin/players/{alice.Id}/gold", new { Change = 50, Reason = "Bug refund." });

        var export = await alice.Client.GetFromJsonAsync<JsonElement>("/api/v1/players/me/export");

        var decision = export.GetProperty("adminDecisions").EnumerateArray().Single();
        Assert.Equal(("AdjustGold", "Bug refund."), (decision.GetProperty("action").GetString(), decision.GetProperty("reason").GetString()));
        Assert.DoesNotContain(boss.Username, export.GetRawText());
        Assert.Equal(JsonValueKind.Null, export.GetProperty("account").GetProperty("suspendedUntil").ValueKind);
    }

    private record Hero(HttpClient Client, Guid Id, string Username, string AccessToken, string RefreshToken, List<string> Roles);

    private static string NewName(string prefix) => $"{prefix}{Guid.NewGuid():N}"[..16];

    // Stands in for changing the Admin:Usernames setting and restarting.
    private static void SetAdmins(GameApiFactory factory, string usernames) =>
        factory.Services.GetRequiredService<IOptions<AdminOptions>>().Value.Usernames = usernames;

    private static async Task<Hero> AdminAsync(GameApiFactory factory)
    {
        var name = NewName("boss");
        SetAdmins(factory, name);
        return await SignUpAsync(factory, username: name);
    }

    private static async Task<Hero> SignUpAsync(GameApiFactory factory, string prefix = "hero", string? username = null)
    {
        var response = await factory.CreateClient().PostAsJsonAsync("/api/v1/auth/register", new { Username = username ?? NewName(prefix), Password });
        response.EnsureSuccessStatusCode();
        return await ToHeroAsync(factory, response);
    }

    private static async Task<Hero> SignInAsync(GameApiFactory factory, string username)
    {
        var response = await SignInResponseAsync(factory, username);
        response.EnsureSuccessStatusCode();
        return await ToHeroAsync(factory, response);
    }

    private static Task<HttpResponseMessage> SignInResponseAsync(GameApiFactory factory, string username) =>
        factory.CreateClient().PostAsJsonAsync("/api/v1/auth/login", new { Username = username, Password });

    private static async Task<Hero> ToHeroAsync(GameApiFactory factory, HttpResponseMessage response)
    {
        var auth = (await response.Content.ReadFromJsonAsync<AuthResponse>(Json))!;
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        return new Hero(client, auth.Player.Id, auth.Player.Username, auth.AccessToken, auth.RefreshToken, auth.Roles);
    }

    private static async Task<T> PostAsync<T>(HttpClient client, string url, object body)
    {
        var response = await client.PostAsJsonAsync(url, body);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<T>(Json))!;
    }

    private static async Task<List<AuditEntryResponse>> AuditAsync(HttpClient client, AdminAction? of = null)
    {
        var entries = await client.GetFromJsonAsync<List<AuditEntryResponse>>("/api/v1/admin/audit?take=50", Json);
        return entries!.Where(e => of is null || e.Action == of).ToList();
    }
}
