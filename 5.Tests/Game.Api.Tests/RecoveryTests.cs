using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Game.Api.Auth;
using Game.Api.Models;
using Game.Api.Recovery;
using Game.Infrastructure.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Game.Api.Tests;

// Account recovery by email: add and confirm an address, then reset a forgotten password with a link.
public partial class RecoveryTests
{
    private const string Password = "Arena-Pass1";
    private const string NewPassword = "Brand-New-Pass2";

    [Fact]
    public async Task AddingAnAddress_SendsAConfirmationLink_AndOnlyTheLinkConfirmsIt()
    {
        using var factory = new GameApiFactory();
        var alice = await SignUpAsync(factory, "alice");

        var wrongPassword = await SetEmailAsync(alice, "alice@example.com", "not-it-at-all");
        var notAnAddress = await SetEmailAsync(alice, "alice at example", Password);
        var added = await SetEmailAsync(alice, "  alice@example.com ", Password);

        Assert.Equal(HttpStatusCode.BadRequest, wrongPassword.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, notAnAddress.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, added.StatusCode);
        Assert.Equal(new RecoveryEmailResponse(null, "alice@example.com"), await added.Content.ReadFromJsonAsync<RecoveryEmailResponse>());

        var email = Assert.Single(factory.Email.To("alice@example.com"));
        Assert.Contains(alice.Username, email.Subject + email.PlainText);
        var link = LinkIn(email);
        Assert.Equal("/confirm-email", link.Path);
        Assert.Equal(alice.Id, link.User);

        var confirmed = await factory.CreateClient().PostAsJsonAsync("/api/v1/auth/confirm-email", new { UserId = link.User, link.Token });
        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
        Assert.Equal(new RecoveryEmailResponse("alice@example.com", null),
            await alice.Client.GetFromJsonAsync<RecoveryEmailResponse>("/api/v1/players/me/recovery-email"));

        // A link works once.
        var again = await factory.CreateClient().PostAsJsonAsync("/api/v1/auth/confirm-email", new { UserId = link.User, link.Token });
        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);
    }

    [Fact]
    public async Task ALink_OnlyWorksForItsOwnAccount_AndOnlyAsIssued()
    {
        using var factory = new GameApiFactory();
        var alice = await SignUpAsync(factory, "alice");
        var bob = await SignUpAsync(factory, "bob");
        await SetEmailAsync(alice, "alice@example.com", Password);
        var link = LinkIn(factory.Email.Sent.Single());

        var forBob = await factory.CreateClient().PostAsJsonAsync("/api/v1/auth/confirm-email", new { UserId = bob.Id, link.Token });
        var tampered = await factory.CreateClient().PostAsJsonAsync("/api/v1/auth/confirm-email", new { UserId = alice.Id, Token = link.Token[..^2] + "xx" });
        var asReset = await factory.CreateClient().PostAsJsonAsync("/api/v1/auth/reset-password", new { UserId = alice.Id, link.Token, NewPassword });

        Assert.Equal(HttpStatusCode.BadRequest, forBob.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, tampered.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, asReset.StatusCode);
    }

    [Fact]
    public async Task AForgottenPassword_CanBeResetFromTheEmailedLink()
    {
        using var factory = new GameApiFactory();
        var alice = await SignUpAsync(factory, "alice");
        await AddConfirmedEmailAsync(factory, alice, "alice@example.com");
        // Someone has been guessing: the account is locked out.
        for (var i = 0; i < 5; i++) await SignInAsync(factory, alice.Username, "wrong-guess-" + i);
        Assert.Equal(HttpStatusCode.Locked, (await SignInAsync(factory, alice.Username, Password)).StatusCode);

        var asked = await factory.CreateClient().PostAsJsonAsync("/api/v1/auth/forgot-password", new { alice.Username });

        Assert.Equal(HttpStatusCode.Accepted, asked.StatusCode);
        var email = factory.Email.To("alice@example.com").Last();
        Assert.Contains("Reset your", email.Subject);
        var link = LinkIn(email);
        Assert.Equal("/reset-password", link.Path);

        var weak = await ResetAsync(factory, link, "short");
        Assert.Equal(HttpStatusCode.BadRequest, weak.StatusCode);

        var reset = await ResetAsync(factory, link, NewPassword);

        Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SignInAsync(factory, alice.Username, Password)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SignInAsync(factory, alice.Username, NewPassword)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await ResetAsync(factory, link, "Another-Pass3")).StatusCode);
    }

    [Fact]
    public async Task ResettingThePassword_SignsOutEveryBrowser()
    {
        using var factory = new GameApiFactory();
        var alice = await SignUpAsync(factory, "alice");
        await AddConfirmedEmailAsync(factory, alice, "alice@example.com");
        await factory.CreateClient().PostAsJsonAsync("/api/v1/auth/forgot-password", new { alice.Username });

        await ResetAsync(factory, LinkIn(factory.Email.Sent.Last()), NewPassword);

        Assert.Equal(HttpStatusCode.Unauthorized, (await alice.Client.GetAsync("/api/v1/players/me")).StatusCode);
        var refresh = await factory.CreateClient().PostAsJsonAsync("/api/v1/auth/refresh", new { alice.RefreshToken });
        Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);
    }

    [Fact]
    public async Task AskingForAReset_NeverSaysWhetherTheHeroOrAddressExists()
    {
        using var factory = new GameApiFactory();
        var noEmail = await SignUpAsync(factory, "plain");
        var unconfirmed = await SignUpAsync(factory, "pending");
        await SetEmailAsync(unconfirmed, "pending@example.com", Password);
        var sentBefore = factory.Email.Sent.Count;

        foreach (var name in new[] { "nobody-by-this-name", noEmail.Username, unconfirmed.Username })
        {
            var response = await factory.CreateClient().PostAsJsonAsync("/api/v1/auth/forgot-password", new { Username = name });
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            Assert.Equal(0, response.Content.Headers.ContentLength ?? 0);
        }

        Assert.Equal(sentBefore, factory.Email.Sent.Count);
    }

    [Fact]
    public async Task ResetLinks_AreNotSentTooOften_AndOnlyTheNewestWorks()
    {
        using var factory = new GameApiFactory();
        var alice = await SignUpAsync(factory, "alice");
        await AddConfirmedEmailAsync(factory, alice, "alice@example.com");
        var client = factory.CreateClient();

        await client.PostAsJsonAsync("/api/v1/auth/forgot-password", new { alice.Username });
        await client.PostAsJsonAsync("/api/v1/auth/forgot-password", new { alice.Username });
        var resets = factory.Email.To("alice@example.com").Where(m => m.Subject.Contains("Reset")).ToList();
        Assert.Single(resets);

        factory.Clock.Advance(TimeSpan.FromMinutes(3));
        await client.PostAsJsonAsync("/api/v1/auth/forgot-password", new { alice.Username });
        resets = factory.Email.To("alice@example.com").Where(m => m.Subject.Contains("Reset")).ToList();

        Assert.Equal(2, resets.Count);
        Assert.Equal(HttpStatusCode.BadRequest, (await ResetAsync(factory, LinkIn(resets[0]), NewPassword)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await ResetAsync(factory, LinkIn(resets[1]), NewPassword)).StatusCode);
    }

    [Fact]
    public async Task AResetLink_ExpiresAfterAnHour()
    {
        using var factory = new GameApiFactory();
        var alice = await SignUpAsync(factory, "alice");
        await AddConfirmedEmailAsync(factory, alice, "alice@example.com");
        await factory.CreateClient().PostAsJsonAsync("/api/v1/auth/forgot-password", new { alice.Username });

        factory.Clock.Advance(TimeSpan.FromMinutes(61));

        Assert.Equal(HttpStatusCode.BadRequest, (await ResetAsync(factory, LinkIn(factory.Email.Sent.Last()), NewPassword)).StatusCode);
    }

    [Fact]
    public async Task RemovingTheAddress_StopsResetLinks_AndDeletingTheHeroDeletesTheLinks()
    {
        using var factory = new GameApiFactory();
        var alice = await SignUpAsync(factory, "alice");
        await AddConfirmedEmailAsync(factory, alice, "alice@example.com");
        var export = await alice.Client.GetFromJsonAsync<JsonElement>("/api/v1/players/me/export");
        Assert.Equal("alice@example.com", export.GetProperty("account").GetProperty("recoveryEmail").GetString());

        var removed = await alice.Client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, "/api/v1/players/me/recovery-email")
        {
            Content = JsonContent.Create(new { Password })
        });
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        var sent = factory.Email.Sent.Count;
        await factory.CreateClient().PostAsJsonAsync("/api/v1/auth/forgot-password", new { alice.Username });
        Assert.Equal(sent, factory.Email.Sent.Count);

        await alice.Client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, "/api/v1/players/me")
        {
            Content = JsonContent.Create(new { Password })
        });
        using var scope = factory.Services.CreateScope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<AppDbContext>().AccountTokens.AnyAsync(t => t.UserId == alice.Id));
    }

    [Fact]
    public async Task WithoutEmailSetUp_RecoveryIsSwitchedOff()
    {
        using var factory = new GameApiFactory();
        using var off = factory.WithWebHostBuilder(b => b.UseSetting("Email:Provider", "None"));
        var client = off.CreateClient();

        var features = await client.GetFromJsonAsync<JsonElement>("/api/v1/features");
        var forgot = await client.PostAsJsonAsync("/api/v1/auth/forgot-password", new { Username = "anyone" });

        Assert.False(features.GetProperty("accountRecovery").GetBoolean());
        Assert.Equal(HttpStatusCode.ServiceUnavailable, forgot.StatusCode);
        Assert.True((await factory.CreateClient().GetFromJsonAsync<JsonElement>("/api/v1/features")).GetProperty("accountRecovery").GetBoolean());
    }

    [Theory]
    [InlineData("alice@example.com", true)]
    [InlineData("a.b+tag@mail.example.co.uk", true)]
    [InlineData("Alice <alice@example.com>", false)]
    [InlineData("alice@localhost", false)]
    [InlineData("alice@", false)]
    [InlineData("", false)]
    public void OnlyDeliverableAddressesAreAccepted(string address, bool accepted) =>
        Assert.Equal(accepted, AccountRecoveryService.Normalize(address) is not null);

    private record Hero(HttpClient Client, Guid Id, string Username, string RefreshToken);

    private record Link(string Path, Guid User, string Token);

    [GeneratedRegex(@"https://play\.test(?<path>/[a-z-]+)\?user=(?<user>[0-9a-f-]+)&token=(?<token>[A-Za-z0-9_-]+)")]
    private static partial Regex LinkPattern();

    private static Link LinkIn(Core.Interfaces.EmailMessage email)
    {
        var match = LinkPattern().Match(email.PlainText);
        Assert.True(match.Success, email.PlainText);
        Assert.Contains(match.Value.Replace("&", "&amp;"), email.Html);
        return new Link(match.Groups["path"].Value, Guid.Parse(match.Groups["user"].Value), match.Groups["token"].Value);
    }

    private static async Task<Hero> SignUpAsync(GameApiFactory factory, string prefix)
    {
        var client = factory.CreateClient();
        var username = $"{prefix}{Guid.NewGuid():N}"[..16];
        var response = await client.PostAsJsonAsync("/api/v1/auth/register", new { Username = username, Password });
        response.EnsureSuccessStatusCode();
        var auth = (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        return new Hero(client, auth.Player.Id, username, auth.RefreshToken);
    }

    private static Task<HttpResponseMessage> SetEmailAsync(Hero hero, string email, string password) =>
        hero.Client.PutAsJsonAsync("/api/v1/players/me/recovery-email", new { Email = email, Password = password });

    private static async Task AddConfirmedEmailAsync(GameApiFactory factory, Hero hero, string email)
    {
        (await SetEmailAsync(hero, email, Password)).EnsureSuccessStatusCode();
        var link = LinkIn(factory.Email.To(email).Last());
        (await factory.CreateClient().PostAsJsonAsync("/api/v1/auth/confirm-email", new { UserId = link.User, link.Token })).EnsureSuccessStatusCode();
    }

    private static Task<HttpResponseMessage> SignInAsync(GameApiFactory factory, string username, string password) =>
        factory.CreateClient().PostAsJsonAsync("/api/v1/auth/login", new { Username = username, Password = password });

    private static Task<HttpResponseMessage> ResetAsync(GameApiFactory factory, Link link, string newPassword) =>
        factory.CreateClient().PostAsJsonAsync("/api/v1/auth/reset-password", new { UserId = link.User, link.Token, NewPassword = newPassword });
}
