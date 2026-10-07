using Microsoft.Playwright;

namespace Game.E2E.Tests;

// The public status page, against the real API (which has no RabbitMQ or blob storage in these tests).
public class StatusPageTests(ArenaFixture arena) : BrowserTest(arena)
{
    [Fact]
    public Task TheVersionInTheHeader_OpensTheStatusPage_WhichShowsEachService() => WithScreenshotsOnFailureAsync(async () =>
    {
        var page = await NewBrowserAsync();
        await page.GotoAsync(Arena.ClientUrl);

        await page.GetByRole(AriaRole.Link, new() { NameRegex = new System.Text.RegularExpressions.Regex(@"^v\d") }).ClickAsync();

        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "System status", Level = 1 })).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Status)).ToContainTextAsync("The game is up, with some features limited");

        var services = page.GetByRole(AriaRole.Region, new() { Name = "Services" }).GetByRole(AriaRole.Listitem);
        await Assertions.Expect(services).ToHaveCountAsync(3);
        await Assertions.Expect(services.Nth(0)).ToContainTextAsync("Operational");
        await Assertions.Expect(services.Nth(0)).ToContainTextAsync("Game database");
        await Assertions.Expect(page.GetByText("No restore drill recorded yet.")).ToBeVisibleAsync();

        // Back to the game, under the same strict Content Security Policy as production.
        await page.GetByRole(AriaRole.Link, new() { Name = "Back to the game" }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Play Game" })).ToBeVisibleAsync();
        Assert.Empty(await page.EvaluateAsync<string[]>("() => window.__cspViolations ?? []"));
    });
}
