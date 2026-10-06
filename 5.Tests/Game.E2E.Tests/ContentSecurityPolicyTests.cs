using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace Game.E2E.Tests;

// The client is served with the same Content Security Policy as in Azure (see ClientHost), so every
// browser test already runs under it. This one walks through each screen, uploads a portrait and
// plays a duel, and fails on anything the policy blocked.
public class ContentSecurityPolicyTests(ArenaFixture arena) : BrowserTest(arena)
{
    // A 1x1 PNG.
    private static readonly byte[] Portrait = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");

    [Fact]
    public void TheClient_IsServedWithAStrictPolicy()
    {
        var policy = Arena.ClientHeaders["Content-Security-Policy"];

        Assert.DoesNotContain("__", policy);                   // every placeholder filled in
        Assert.DoesNotContain("'unsafe-eval'", policy);        // WebAssembly only needs 'wasm-unsafe-eval'
        Assert.Matches("script-src 'self' 'wasm-unsafe-eval' 'sha256-[A-Za-z0-9+/]+=*';", policy);
        Assert.Contains("frame-ancestors 'none'", policy);
        Assert.Contains("object-src 'none'", policy);
    }

    [Fact]
    public async Task TheApi_RunningOnKestrel_SendsItsHeadersAndNoServerName()
    {
        using var http = new HttpClient();

        var response = await http.GetAsync($"{Arena.ApiUrl}/health/live");

        Assert.Empty(response.Headers.Server);
        Assert.Equal("default-src 'none'; frame-ancestors 'none'",
            string.Join(",", response.Headers.GetValues("Content-Security-Policy")));
    }

    [Fact]
    public Task PlayingTheGame_TripsNothingInThePolicy() => WithScreenshotsOnFailureAsync(async () =>
    {
        var alice = await CreateHeroAsync(NewHeroName("Csp"), heroClass: "Sorcerer");
        var bob = await CreateHeroAsync(NewHeroName("Csp"), heroClass: "Paladin");

        // Portrait upload: the new image comes from the storage origin the policy allows.
        await alice.GetByRole(AriaRole.Button, new() { Name = "Change portrait" }).ClickAsync();
        await alice.GetByLabel("Portrait image (PNG or JPG)").SetInputFilesAsync(
            new FilePayload { Name = "hero.png", MimeType = "image/png", Buffer = Portrait });
        await alice.GetByRole(AriaRole.Button, new() { Name = "Save portrait" }).ClickAsync();
        await Assertions.Expect(alice.GetByRole(AriaRole.Dialog)).ToBeHiddenAsync();

        await alice.GetByRole(AriaRole.Button, new() { Name = "Leaderboards" }).ClickAsync();
        await Assertions.Expect(alice.GetByText("GLOBAL HERO RANKINGS")).ToBeVisibleAsync();
        await alice.GetByRole(AriaRole.Button, new() { Name = "Town Dashboard" }).ClickAsync();
        await alice.GetByRole(AriaRole.Button, new() { Name = "GOLD SHOP" }).ClickAsync();
        await Assertions.Expect(alice.GetByText("Card Upgrades")).ToBeVisibleAsync();
        await alice.GetByRole(AriaRole.Button, new() { Name = "Back to Town" }).First.ClickAsync();

        await alice.GetByRole(AriaRole.Button, new() { Name = "LAUNCH BATTLE ARENA" }).ClickAsync();
        await Assertions.Expect(alice.GetByText("Next boss move")).ToBeVisibleAsync();
        await alice.GetByRole(AriaRole.Button, new() { NameRegex = new Regex("Fireball") }).ClickAsync();
        await Assertions.Expect(alice.GetByText(new Regex("Turn 1: You cast Fireball"))).ToBeVisibleAsync();

        // A duel runs over the SignalR WebSocket, which connect-src must allow.
        var carol = await CreateHeroAsync(NewHeroName("Csp"), heroClass: "Ranger");
        foreach (var page in new[] { bob, carol })
        {
            await page.GetByRole(AriaRole.Button, new() { Name = "FIND AN OPPONENT" }).ClickAsync();
            await page.Locator("button[aria-label=\"Friendly duel\"]").ClickAsync();
        }
        await Assertions.Expect(bob.GetByText(new Regex("Your turn|Waiting for"))).ToBeVisibleAsync();

        foreach (var page in new[] { alice, bob, carol })
        {
            Assert.Empty(await ViolationsAsync(page));
        }
    });

    // BrowserTest records every securitypolicyviolation event the page fires.
    private static Task<string[]> ViolationsAsync(IPage page) =>
        page.EvaluateAsync<string[]>("() => window.__cspViolations ?? []");
}
