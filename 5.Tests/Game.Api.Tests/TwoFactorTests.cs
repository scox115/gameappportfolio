using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Game.Api.Models;
using Game.Api.Options;
using Game.Api.TwoFactor;
using Game.Core.Admin;
using Game.Core.Security;
using Game.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Game.Api.Tests;

// Two-factor sign-in: setting up an authenticator app, signing in with its codes or a recovery code,
// and turning it off again.
public class TwoFactorTests
{
    private const string Password = "Arena-Pass1";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    [Fact]
    public async Task TurningItOn_NeedsThePassword_ThenACodeFromTheApp()
    {
        using var factory = new GameApiFactory();
        var hero = await SignUpAsync(factory);

        var wrongPassword = await hero.Client.PostAsJsonAsync("/api/v1/players/me/two-factor/authenticator", new { Password = "not-it-at-all" });
        Assert.Equal(HttpStatusCode.BadRequest, wrongPassword.StatusCode);

        var setup = await BeginSetupAsync(hero);
        Assert.Matches("^([A-Z2-7]{4} )+[A-Z2-7]{1,4}$", setup.SharedKey);
        Assert.StartsWith("otpauth://totp/Kings%20of%20the%20Card%20Arena%3A" + hero.Username + "?secret=", setup.AuthenticatorUri);
        Assert.Contains("&issuer=Kings%20of%20the%20Card%20Arena", setup.AuthenticatorUri);

        var wrongCode = await hero.Client.PostAsJsonAsync("/api/v1/players/me/two-factor/enable", new { Code = "000000" });
        Assert.Equal(HttpStatusCode.BadRequest, wrongCode.StatusCode);
        Assert.False((await StatusAsync(hero)).Enabled);

        var enabled = await hero.Client.PostAsJsonAsync("/api/v1/players/me/two-factor/enable", new { Code = CodeNow(factory, setup) });
        enabled.EnsureSuccessStatusCode();
        var codes = (await enabled.Content.ReadFromJsonAsync<RecoveryCodes>(Json))!.Codes;

        Assert.Equal(10, codes.Distinct().Count());
        Assert.All(codes, c => Assert.Matches("^[A-HJKMNP-Z2-9]{5}-[A-HJKMNP-Z2-9]{5}$", c));
        Assert.Equal(new TwoFactorStatus(true, 10), await StatusAsync(hero));

        // Only hashes of the recovery codes are stored.
        using var scope = factory.Services.CreateScope();
        var stored = await scope.ServiceProvider.GetRequiredService<AppDbContext>().UserTokens
            .Where(t => t.UserId == hero.Id).Select(t => t.Value).ToListAsync();
        Assert.DoesNotContain(stored, value => codes.Any(c => value!.Contains(c) || value.Contains(c.Replace("-", ""))));
    }

    [Fact]
    public async Task SigningIn_AsksForTheCode_OnlyOnceThePasswordIsRight()
    {
        using var factory = new GameApiFactory();
        var (hero, setup, _) = await TurnOnAsync(factory);
        var client = factory.CreateClient();

        var wrongPassword = await LoginAsync(client, hero.Username, "Wrong-Pass1");
        Assert.Equal(HttpStatusCode.Unauthorized, wrongPassword.StatusCode);
        Assert.DoesNotContain("twoFactorRequired", await wrongPassword.Content.ReadAsStringAsync());

        var noCode = await LoginAsync(client, hero.Username, Password);
        Assert.Equal(HttpStatusCode.Unauthorized, noCode.StatusCode);
        Assert.True((await ProblemAsync(noCode)).GetProperty("twoFactorRequired").GetBoolean());

        factory.Clock.Advance(Totp.StepLength);
        var withCode = await LoginAsync(client, hero.Username, Password, CodeNow(factory, setup));
        Assert.Equal(HttpStatusCode.OK, withCode.StatusCode);
    }

    [Fact]
    public async Task ACodeFromTheApp_WorksOnlyOnce()
    {
        using var factory = new GameApiFactory();
        var (hero, setup, _) = await TurnOnAsync(factory);
        var client = factory.CreateClient();
        factory.Clock.Advance(Totp.StepLength);
        var code = CodeNow(factory, setup);

        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(client, hero.Username, Password, code)).StatusCode);
        var replay = await LoginAsync(client, hero.Username, Password, code);
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
        Assert.Contains("isn't right", (await ProblemAsync(replay)).GetProperty("detail").GetString());

        factory.Clock.Advance(Totp.StepLength);
        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(client, hero.Username, Password, CodeNow(factory, setup))).StatusCode);
    }

    [Fact]
    public async Task ARecoveryCode_SignsInOnce_InPlaceOfTheApp()
    {
        using var factory = new GameApiFactory();
        var (hero, _, codes) = await TurnOnAsync(factory);
        var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(client, hero.Username, Password, codes[3].ToLowerInvariant())).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(client, hero.Username, Password, codes[3])).StatusCode);

        var signedIn = await SignInAsync(factory, hero.Username, codes[4].Replace("-", ""));
        Assert.Equal(new TwoFactorStatus(true, 8), await StatusAsync(signedIn));
    }

    [Fact]
    public async Task GuessingCodes_LocksTheAccount_EvenWithTheRightPassword()
    {
        using var factory = new GameApiFactory();
        var (hero, setup, _) = await TurnOnAsync(factory);
        var client = factory.CreateClient();

        for (var guess = 1; guess < 5; guess++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(client, hero.Username, Password, $"{guess:D6}")).StatusCode);
        }
        Assert.Equal(HttpStatusCode.Locked, (await LoginAsync(client, hero.Username, Password, "000005")).StatusCode);

        factory.Clock.Advance(Totp.StepLength);
        Assert.Equal(HttpStatusCode.Locked, (await LoginAsync(client, hero.Username, Password, CodeNow(factory, setup))).StatusCode);
    }

    [Fact]
    public async Task TurningItOff_NeedsThePasswordAndACode_ThenThePasswordIsEnough()
    {
        using var factory = new GameApiFactory();
        var (hero, setup, codes) = await TurnOnAsync(factory);
        factory.Clock.Advance(Totp.StepLength);

        var noCode = await hero.Client.PostAsJsonAsync("/api/v1/players/me/two-factor/disable", new { Password, Code = "" });
        Assert.Equal(HttpStatusCode.BadRequest, noCode.StatusCode);
        var wrongPassword = await hero.Client.PostAsJsonAsync("/api/v1/players/me/two-factor/disable", new { Password = "Wrong-Pass1", Code = CodeNow(factory, setup) });
        Assert.Equal(HttpStatusCode.BadRequest, wrongPassword.StatusCode);
        Assert.True((await StatusAsync(hero)).Enabled);

        var off = await hero.Client.PostAsJsonAsync("/api/v1/players/me/two-factor/disable", new { Password, Code = codes[0] });
        Assert.Equal(HttpStatusCode.NoContent, off.StatusCode);
        Assert.Equal(new TwoFactorStatus(false, 0), await StatusAsync(hero));
        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(factory.CreateClient(), hero.Username, Password)).StatusCode);

        using var scope = factory.Services.CreateScope();
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<AppDbContext>().UserTokens.Where(t => t.UserId == hero.Id).ToListAsync());
    }

    [Fact]
    public async Task NewRecoveryCodes_ReplaceTheOldOnes()
    {
        using var factory = new GameApiFactory();
        var (hero, setup, oldCodes) = await TurnOnAsync(factory);
        factory.Clock.Advance(Totp.StepLength);

        var response = await hero.Client.PostAsJsonAsync("/api/v1/players/me/two-factor/recovery-codes", new { Code = CodeNow(factory, setup) });
        response.EnsureSuccessStatusCode();
        var newCodes = (await response.Content.ReadFromJsonAsync<RecoveryCodes>(Json))!.Codes;

        Assert.Empty(newCodes.Intersect(oldCodes));
        var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(client, hero.Username, Password, oldCodes[0])).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(client, hero.Username, Password, newCodes[0])).StatusCode);
    }

    [Fact]
    public async Task SettingUpAgain_IsRefusedWhileItIsOn()
    {
        using var factory = new GameApiFactory();
        var (hero, _, _) = await TurnOnAsync(factory);

        var again = await hero.Client.PostAsJsonAsync("/api/v1/players/me/two-factor/authenticator", new { Password });

        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
    }

    [Fact]
    public async Task AnAdmin_CanTurnItOffForAPlayerWhoLostTheirPhone_WithAReasonInTheAuditLog()
    {
        using var factory = new GameApiFactory();
        var (hero, _, _) = await TurnOnAsync(factory);
        var adminName = "boss" + Guid.NewGuid().ToString("N")[..8];
        factory.Services.GetRequiredService<IOptions<AdminOptions>>().Value.Usernames = adminName;
        var admin = await SignUpAsync(factory, adminName);

        var detail = await admin.Client.GetFromJsonAsync<AdminPlayerDetail>($"/api/v1/admin/players/{hero.Id}", Json);
        Assert.True(detail!.TwoFactorEnabled);

        var noReason = await admin.Client.PostAsJsonAsync($"/api/v1/admin/players/{hero.Id}/turn-off-two-factor", new { Reason = "" });
        Assert.Equal(HttpStatusCode.BadRequest, noReason.StatusCode);

        var off = await admin.Client.PostAsJsonAsync($"/api/v1/admin/players/{hero.Id}/turn-off-two-factor", new { Reason = "Lost phone; confirmed by recovery email." });
        off.EnsureSuccessStatusCode();
        detail = await off.Content.ReadFromJsonAsync<AdminPlayerDetail>(Json);

        Assert.False(detail!.TwoFactorEnabled);
        var entry = Assert.Single(detail.History);
        Assert.Equal(AdminAction.TurnOffTwoFactor, entry.Action);
        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(factory.CreateClient(), hero.Username, Password)).StatusCode);

        var twice = await admin.Client.PostAsJsonAsync($"/api/v1/admin/players/{hero.Id}/turn-off-two-factor", new { Reason = "Again" });
        Assert.Equal(HttpStatusCode.Conflict, twice.StatusCode);
    }

    [Fact]
    public async Task TheDataExport_SaysWhetherItIsOn_AndDeletingTheHeroDeletesTheKey()
    {
        using var factory = new GameApiFactory();
        var (hero, _, codes) = await TurnOnAsync(factory);

        var export = await hero.Client.GetFromJsonAsync<JsonElement>("/api/v1/players/me/export");
        Assert.True(export.GetProperty("account").GetProperty("twoFactorEnabled").GetBoolean());
        Assert.DoesNotContain(codes[0], export.GetRawText());

        var delete = await hero.Client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, "/api/v1/players/me")
        {
            Content = JsonContent.Create(new { Password, Confirmation = hero.Username })
        });
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

        using var scope = factory.Services.CreateScope();
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<AppDbContext>().UserTokens.Where(t => t.UserId == hero.Id).ToListAsync());
    }

    private sealed record Hero(HttpClient Client, Guid Id, string Username);

    private static string CodeNow(GameApiFactory factory, AuthenticatorSetup setup) =>
        Totp.Code(Base32.Decode(setup.SharedKey), Totp.StepAt(factory.Clock.GetUtcNow()));

    private static async Task<(Hero Hero, AuthenticatorSetup Setup, List<string> Codes)> TurnOnAsync(GameApiFactory factory)
    {
        var hero = await SignUpAsync(factory);
        var setup = await BeginSetupAsync(hero);
        var enabled = await hero.Client.PostAsJsonAsync("/api/v1/players/me/two-factor/enable", new { Code = CodeNow(factory, setup) });
        enabled.EnsureSuccessStatusCode();
        return (hero, setup, (await enabled.Content.ReadFromJsonAsync<RecoveryCodes>(Json))!.Codes);
    }

    private static async Task<AuthenticatorSetup> BeginSetupAsync(Hero hero)
    {
        var response = await hero.Client.PostAsJsonAsync("/api/v1/players/me/two-factor/authenticator", new { Password });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AuthenticatorSetup>(Json))!;
    }

    private static async Task<TwoFactorStatus> StatusAsync(Hero hero) =>
        (await hero.Client.GetFromJsonAsync<TwoFactorStatus>("/api/v1/players/me/two-factor", Json))!;

    private static Task<HttpResponseMessage> LoginAsync(HttpClient client, string username, string password, string? code = null) =>
        client.PostAsJsonAsync("/api/v1/auth/login", new { Username = username, Password = password, TwoFactorCode = code });

    private static async Task<JsonElement> ProblemAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>();

    private static async Task<Hero> SignUpAsync(GameApiFactory factory, string? username = null)
    {
        username ??= "tf" + Guid.NewGuid().ToString("N")[..10];
        var response = await factory.CreateClient().PostAsJsonAsync("/api/v1/auth/register", new { Username = username, Password });
        response.EnsureSuccessStatusCode();
        return Authorized(factory, (await response.Content.ReadFromJsonAsync<AuthResponse>(Json))!);
    }

    private static async Task<Hero> SignInAsync(GameApiFactory factory, string username, string code)
    {
        var response = await LoginAsync(factory.CreateClient(), username, Password, code);
        response.EnsureSuccessStatusCode();
        return Authorized(factory, (await response.Content.ReadFromJsonAsync<AuthResponse>(Json))!);
    }

    private static Hero Authorized(GameApiFactory factory, AuthResponse auth)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        return new Hero(client, auth.Player.Id, auth.Player.Username);
    }
}
