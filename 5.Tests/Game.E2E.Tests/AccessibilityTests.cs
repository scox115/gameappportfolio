using System.Text;
using System.Text.RegularExpressions;
using Deque.AxeCore.Commons;
using Deque.AxeCore.Playwright;
using Microsoft.Playwright;

namespace Game.E2E.Tests;

// Scans each screen with axe-core against WCAG 2.1 A and AA, the level most accessibility laws
// point to. axe catches roughly half of real problems (missing labels, low contrast, broken ARIA);
// keyboard play is checked separately below.
public class AccessibilityTests(ArenaFixture arena) : BrowserTest(arena)
{
    private static readonly AxeRunOptions Wcag21AA = new()
    {
        RunOnly = new RunOnlyOptions { Type = "tag", Values = ["wcag2a", "wcag2aa", "wcag21a", "wcag21aa"] },
    };

    [Fact]
    public Task TheSignInAndCreateHeroForms_HaveNoViolations() => WithScreenshotsOnFailureAsync(async () =>
    {
        var page = await NewBrowserAsync();
        await page.GotoAsync(Arena.ClientUrl);
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Play Game" })).ToBeVisibleAsync();
        await AssertNoViolationsAsync(page, "sign-in");

        await page.GetByRole(AriaRole.Button, new() { Name = "Create a new hero" }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Create Hero" })).ToBeVisibleAsync();
        await AssertNoViolationsAsync(page, "create hero");
    });

    [Fact]
    public Task TheTownLeaderboardAndShop_HaveNoViolations() => WithScreenshotsOnFailureAsync(async () =>
    {
        var page = await CreateHeroAsync(NewHeroName("A11y"));
        await AssertNoViolationsAsync(page, "town");

        await page.GetByRole(AriaRole.Button, new() { Name = "Leaderboards" }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Tab).First).ToBeVisibleAsync();
        await AssertNoViolationsAsync(page, "leaderboard");

        await page.GetByRole(AriaRole.Button, new() { Name = "Town Dashboard" }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "GOLD SHOP" }).ClickAsync();
        await Assertions.Expect(page.GetByText("Gold Shop").First).ToBeVisibleAsync();
        await AssertNoViolationsAsync(page, "gold shop");
    });

    [Fact]
    public Task TheBossFightAndItsResult_HaveNoViolations() => WithScreenshotsOnFailureAsync(async () =>
    {
        var page = await CreateHeroAsync(NewHeroName("A11y"), heroClass: "Sorcerer");
        await page.GetByRole(AriaRole.Button, new() { Name = "LAUNCH BATTLE ARENA" }).ClickAsync();
        await Assertions.Expect(page.GetByText("Next boss move")).ToBeVisibleAsync();
        await AssertNoViolationsAsync(page, "boss fight");

        var heading = page.GetByRole(AriaRole.Heading, new() { NameRegex = new Regex("^(VICTORY|DEFEAT)$") });
        for (var click = 0; click < 100 && !await heading.IsVisibleAsync(); click++)
        {
            await page.GetByRole(AriaRole.Button, new() { NameRegex = new Regex("Fireball") }).Or(heading).First
                .ClickAsync(new() { Timeout = 2_000 }).ContinueWith(_ => { });
            await page.WaitForTimeoutAsync(200);
        }
        await Assertions.Expect(heading).ToBeVisibleAsync();
        await AssertNoViolationsAsync(page, "battle result");
    });

    [Fact]
    public Task TheDuelLobbyAndDuel_HaveNoViolations() => WithScreenshotsOnFailureAsync(async () =>
    {
        var alice = await CreateHeroAsync(NewHeroName("A11y"));
        await alice.GetByRole(AriaRole.Button, new() { Name = "FIND AN OPPONENT" }).ClickAsync();
        await Assertions.Expect(alice.Locator("button[aria-label=\"Friendly duel\"]")).ToBeVisibleAsync();
        await AssertNoViolationsAsync(alice, "duel lobby");

        var bob = await CreateHeroAsync(NewHeroName("A11y"));
        await bob.GetByRole(AriaRole.Button, new() { Name = "FIND AN OPPONENT" }).ClickAsync();
        await alice.Locator("button[aria-label=\"Friendly duel\"]").ClickAsync();
        await bob.Locator("button[aria-label=\"Friendly duel\"]").ClickAsync();
        await Assertions.Expect(alice.GetByText(new Regex("Your turn|Waiting for"))).ToBeVisibleAsync();
        await AssertNoViolationsAsync(alice, "duel");
    });

    [Fact]
    public Task TheBossCanBeFoughtWithTheKeyboardAlone() => WithScreenshotsOnFailureAsync(async () =>
    {
        var name = NewHeroName("Keys");
        var page = await NewBrowserAsync();
        await page.GotoAsync(Arena.ClientUrl);

        // Sign up: Tab to each control, type, and press Enter or Space; no mouse at all.
        await TabToAsync(page, page.GetByRole(AriaRole.Button, new() { Name = "Create a new hero" }));
        await page.Keyboard.PressAsync("Enter");
        await TabToAsync(page, page.GetByPlaceholder("Enter Character name..."));
        await page.Keyboard.TypeAsync(name);
        await page.Keyboard.PressAsync("Tab");
        await page.Keyboard.TypeAsync(Password);
        await TabToAsync(page, page.Locator("button[aria-pressed]:has-text(\"Sorcerer\")"));
        await page.Keyboard.PressAsync("Space");
        await TabToAsync(page, page.GetByRole(AriaRole.Button, new() { Name = "Create Hero" }));
        await page.Keyboard.PressAsync("Enter");

        var launch = page.GetByRole(AriaRole.Button, new() { Name = "LAUNCH BATTLE ARENA" });
        await TabToAsync(page, launch);
        await page.Keyboard.PressAsync("Enter");
        await Assertions.Expect(page.GetByText("Next boss move")).ToBeVisibleAsync();

        // Focus Fireball once, then keep pressing Enter: the button keeps focus between turns.
        var heading = page.GetByRole(AriaRole.Heading, new() { NameRegex = new Regex("^(VICTORY|DEFEAT)$") });
        await TabToAsync(page, page.GetByRole(AriaRole.Button, new() { NameRegex = new Regex("Fireball") }));
        for (var press = 0; press < 100 && !await heading.IsVisibleAsync(); press++)
        {
            await page.Keyboard.PressAsync("Enter");
            await page.WaitForTimeoutAsync(250);
        }

        await Assertions.Expect(heading).ToBeVisibleAsync();
        await TabToAsync(page, page.GetByRole(AriaRole.Button, new() { Name = "Return to Town" }));
        await page.Keyboard.PressAsync("Enter");
        await Assertions.Expect(launch).ToBeVisibleAsync();
    });

    [Fact]
    public Task ThePortraitPopup_TakesFocusAndClosesWithEscape() => WithScreenshotsOnFailureAsync(async () =>
    {
        var page = await CreateHeroAsync(NewHeroName("Keys"));

        await page.GetByRole(AriaRole.Button, new() { Name = "Change portrait" }).ClickAsync();
        var dialog = page.GetByRole(AriaRole.Dialog);
        await Assertions.Expect(dialog.GetByRole(AriaRole.Button, new() { Name = "Close" })).ToBeFocusedAsync();
        await AssertNoViolationsAsync(page, "portrait popup");

        await page.Keyboard.PressAsync("Escape");
        await Assertions.Expect(dialog).ToBeHiddenAsync();
    });

    // Presses Tab until the target has focus, the way a keyboard user gets around.
    [Fact]
    public Task TheAccountDialog_TakesFocusAndClosesWithEscape() => WithScreenshotsOnFailureAsync(async () =>
    {
        var page = await CreateHeroAsync(NewHeroName("Keys"));

        await page.GetByRole(AriaRole.Button, new() { Name = "Your account and data" }).ClickAsync();
        var dialog = page.GetByRole(AriaRole.Dialog, new() { Name = "Your account" });
        await Assertions.Expect(dialog.GetByRole(AriaRole.Button, new() { Name = "Close" })).ToBeFocusedAsync();
        await AssertNoViolationsAsync(page, "account dialog");

        await page.Keyboard.PressAsync("Escape");
        await Assertions.Expect(dialog).ToBeHiddenAsync();
    });

    [Fact]
    public Task TheAdminTools_HaveNoViolations() => WithScreenshotsOnFailureAsync(async () =>
    {
        var player = NewHeroName("Seen");
        await CreateHeroAsync(player);
        var page = await CreateHeroAsync(ApiHost.AdminNames[1]);

        await page.GetByRole(AriaRole.Link, new() { Name = "Admin tools" }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = player }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = player })).ToBeVisibleAsync();
        await AssertNoViolationsAsync(page, "admin tools");
    });

    [Theory]
    [InlineData("terms", "Terms of Service")]
    [InlineData("privacy", "Privacy Policy")]
    [InlineData("forgot-password", "Forgot your password?")]
    [InlineData("reset-password?user=00000000-0000-0000-0000-000000000001&token=abc", "Choose a new password")]
    public Task TheInfoPages_HaveNoViolations(string path, string heading) => WithScreenshotsOnFailureAsync(async () =>
    {
        var page = await NewBrowserAsync();
        await page.GotoAsync($"{Arena.ClientUrl}/{path}");
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = heading, Level = 1 })).ToBeVisibleAsync();
        await AssertNoViolationsAsync(page, heading);
    });

    private static async Task TabToAsync(IPage page, ILocator target, int maxPresses = 60)
    {
        await Assertions.Expect(target).ToBeVisibleAsync();
        for (var press = 0; press < maxPresses; press++)
        {
            if (await target.EvaluateAsync<bool>("el => el === document.activeElement")) return;
            await page.Keyboard.PressAsync("Tab");
        }
        Assert.Fail($"Couldn't reach {target} with the Tab key in {maxPresses} presses.");
    }

    [Fact]
    public Task TheStatusPage_HasNoViolations() => WithScreenshotsOnFailureAsync(async () =>
    {
        var page = await NewBrowserAsync();
        await page.GotoAsync($"{Arena.ClientUrl}/status");
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Services" })).ToBeVisibleAsync();
        await AssertNoViolationsAsync(page, "status");
    });

    private static async Task AssertNoViolationsAsync(IPage page, string screen)
    {
        // Let fades and number count-ups finish, or axe measures half-drawn colours.
        await page.WaitForTimeoutAsync(500);
        var result = await page.RunAxe(Wcag21AA);
        if (result.Violations.Length == 0) return;

        var report = new StringBuilder($"Accessibility problems on the {screen} screen:\n");
        foreach (var violation in result.Violations)
        {
            report.AppendLine($"- {violation.Id} ({violation.Impact}): {violation.Help}");
            foreach (var node in violation.Nodes.Take(5))
            {
                report.AppendLine($"    {node.Target}  {node.Html}");
            }
        }
        Assert.Fail(report.ToString());
    }
}
