using Microsoft.Playwright;

namespace Game.E2E.Tests;

// One click to try the game as a guest, then keep the hero with a name and password.
public class GuestPlayTests(ArenaFixture arena) : BrowserTest(arena)
{
    [Fact]
    public Task AGuest_CanPlayAtOnce_AndKeepTheirHeroLater() => WithScreenshotsOnFailureAsync(async () =>
    {
        var page = await NewBrowserAsync();
        await page.GotoAsync(Arena.ClientUrl);
        await page.GetByRole(AriaRole.Button, new() { Name = "Play as a guest" }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "LAUNCH BATTLE ARENA" })).ToBeVisibleAsync();

        var guestBox = page.GetByRole(AriaRole.Region, new() { Name = "You're playing as a guest" });
        await Assertions.Expect(guestBox).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Your account and data" })).ToHaveCountAsync(0);

        // Friendly duels are open to guests; wagers wait until the hero is kept.
        await page.GetByRole(AriaRole.Button, new() { Name = "FIND AN OPPONENT" }).ClickAsync();
        await Assertions.Expect(page.Locator("button[aria-label=\"Friendly duel\"]")).ToBeEnabledAsync();
        await Assertions.Expect(page.Locator("button[aria-label=\"Wager 50 gold\"]")).ToBeDisabledAsync();
        await Assertions.Expect(page.GetByText("Save your hero in town to duel for gold.")).ToBeVisibleAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Back to Town" }).ClickAsync();

        var name = NewHeroName("Kept");
        await guestBox.GetByRole(AriaRole.Button, new() { Name = "Keep this hero" }).ClickAsync();
        await guestBox.GetByLabel("Hero name").FillAsync(name);
        await guestBox.GetByLabel("Password (at least 8 characters)").FillAsync(Password);
        await guestBox.GetByRole(AriaRole.Button, new() { Name = "Keep my hero" }).ClickAsync();

        await Assertions.Expect(guestBox).ToHaveCountAsync(0);
        await Assertions.Expect(page.GetByText(name).First).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Your account and data" })).ToBeVisibleAsync();

        // The kept hero signs in like any other, from another browser.
        await SignInAsync(name);
        Assert.Empty(await page.EvaluateAsync<string[]>("() => window.__cspViolations ?? []"));
    });
}
