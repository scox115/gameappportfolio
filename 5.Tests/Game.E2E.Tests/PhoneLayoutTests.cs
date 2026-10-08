using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace Game.E2E.Tests;

// The game on a phone-sized screen (390 by 844, an iPhone 14): every screen fits the width, and the
// battle cards share a row without covering anything.
public class PhoneLayoutTests(ArenaFixture arena) : BrowserTest(arena)
{
    [Fact]
    public Task EveryScreen_FitsAPhone() => WithScreenshotsOnFailureAsync(async () =>
    {
        var page = await CreateHeroAsync(NewHeroName("Phone"), heroClass: "Sorcerer");
        await page.SetViewportSizeAsync(390, 844);
        await FitsAsync(page, "town");

        await page.GetByRole(AriaRole.Button, new() { Name = "LAUNCH BATTLE ARENA" }).ClickAsync();
        await Assertions.Expect(page.GetByText("Next boss move")).ToBeVisibleAsync();
        await FitsAsync(page, "boss fight");
        await CardsShareARowAsync(page);
        await page.GetByRole(AriaRole.Button, new() { NameRegex = new Regex("Town") }).First.ClickAsync();

        await page.GetByRole(AriaRole.Button, new() { Name = "GOLD SHOP" }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "🪙 Gold Shop" })).ToBeVisibleAsync();
        await FitsAsync(page, "Gold Shop");
        await page.GetByRole(AriaRole.Button, new() { Name = "🏰 Back to Town" }).First.ClickAsync();

        await page.GetByRole(AriaRole.Button, new() { Name = "Leaderboards" }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { NameRegex = new Regex("GLOBAL HERO RANKINGS") })).ToBeVisibleAsync();
        await FitsAsync(page, "leaderboards");
        await page.GetByRole(AriaRole.Button, new() { Name = "🏰 Town Dashboard" }).ClickAsync();

        await page.GetByRole(AriaRole.Button, new() { Name = "FIND AN OPPONENT" }).ClickAsync();
        await page.Locator("button[aria-label=\"Friendly duel\"]").ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Practice against the Arena Bot" }).ClickAsync();
        await Assertions.Expect(page.GetByText("Practice duel against the Arena Bot")).ToBeVisibleAsync();
        await FitsAsync(page, "duel");
        await CardsShareARowAsync(page);

        // The Forfeit button sits below the cards, where a tap reaches it.
        await page.GetByRole(AriaRole.Button, new() { Name = "🏳️ Forfeit" }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "DEFEAT" })).ToBeVisibleAsync();
        await FitsAsync(page, "duel result");
    });

    [Fact]
    public Task TheSignInScreen_FitsAPhone() => WithScreenshotsOnFailureAsync(async () =>
    {
        var visitor = await NewBrowserAsync();
        await visitor.SetViewportSizeAsync(390, 844);
        await visitor.GotoAsync(Arena.ClientUrl);
        await Assertions.Expect(visitor.Locator(".live-pulse")).ToBeVisibleAsync();

        await FitsAsync(visitor, "sign-in");
    });

    private static async Task FitsAsync(IPage page, string screen)
    {
        await page.WaitForTimeoutAsync(300); // let the screen finish rendering
        var overflow = await page.EvaluateAsync<int>("document.documentElement.scrollWidth - document.documentElement.clientWidth");
        Assert.True(overflow <= 0, $"The {screen} screen is {overflow}px wider than a phone.");
    }

    // All three cards in one row, inside the screen, none overlapping the next.
    private static async Task CardsShareARowAsync(IPage page)
    {
        var boxes = new List<LocatorBoundingBoxResult>();
        foreach (var name in new[] { "Fireball", "Holy Shield", "Dragon Claw" })
        {
            boxes.Add((await page.GetByRole(AriaRole.Button, new() { NameRegex = new Regex(name) }).BoundingBoxAsync())!);
        }

        Assert.All(boxes, b => Assert.True(b.X >= 0 && b.X + b.Width <= 390, $"A card runs off the screen at x={b.X}, width {b.Width}."));
        Assert.All(boxes, b => Assert.Equal(boxes[0].Y, b.Y, 1.0));
        for (var i = 1; i < boxes.Count; i++)
        {
            Assert.True(boxes[i].X >= boxes[i - 1].X + boxes[i - 1].Width, "Two cards overlap.");
        }
    }
}
