using Microsoft.Playwright;

namespace Game.E2E.Tests;

// Signing in on the sign-in screen.
public class SignInTests(ArenaFixture arena) : BrowserTest(arena)
{
    [Fact]
    public Task ADoubleTapOnPlayGame_SignsInOnce() => WithScreenshotsOnFailureAsync(async () =>
    {
        var name = NewHeroName("Tap");
        await (await CreateHeroAsync(name)).CloseAsync();

        var page = await NewBrowserAsync();
        await page.GotoAsync(Arena.ClientUrl);
        await page.GetByPlaceholder("Enter Character name...").FillAsync(name);
        await page.GetByPlaceholder("At least 8 characters...").FillAsync(Password);
        await page.GetByRole(AriaRole.Button, new() { Name = "Play Game" }).DblClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "LAUNCH BATTLE ARENA" })).ToBeVisibleAsync();

        // A second sign-in would end the first one's session and send the hero back to the sign-in screen.
        await page.WaitForTimeoutAsync(3000);
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "LAUNCH BATTLE ARENA" })).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByText("You signed in on another browser")).ToHaveCountAsync(0);
    });
}
