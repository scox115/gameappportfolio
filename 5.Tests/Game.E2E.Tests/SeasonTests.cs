using Microsoft.Playwright;

namespace Game.E2E.Tests;

// Monthly ranked seasons: the hero's season in town, and the live season standings on the
// leaderboards (docs/adr/0035-ranked-seasons.md). Closing a season is tested in the API tests.
public class SeasonTests(ArenaFixture arena) : BrowserTest(arena)
{
    [Fact]
    public Task AHero_SeesTheirSeason_AndClimbsTheSeasonStandings() => WithScreenshotsOnFailureAsync(async () =>
    {
        var name = NewHeroName("Season");
        var page = await CreateHeroAsync(name);

        var card = page.Locator(".season-card");
        await Assertions.Expect(card).ToContainTextAsync("Ranked season");
        await Assertions.Expect(card).ToContainTextAsync("Fight 3 more duels this season to be ranked");

        await page.GetByRole(AriaRole.Button, new() { Name = "Leaderboards" }).ClickAsync();
        var standings = page.Locator(".season-standings");
        await Assertions.Expect(standings.GetByLabel("Season")).ToContainTextAsync("(now)");
        var myRow = standings.Locator("tbody tr", new() { HasText = name });
        await Assertions.Expect(myRow).ToHaveCountAsync(0);

        // Three duels this season put the hero in the standings; the open page reloads them by itself.
        await Arena.RecordSeasonDuelsAsync(name, wins: 3, losses: 0, rating: 9_900);

        await Assertions.Expect(myRow).ToContainTextAsync("9900");
        await Assertions.Expect(myRow).ToContainTextAsync("(You)");
        await Assertions.Expect(myRow).ToContainTextAsync("3-0");
        await Assertions.Expect(myRow.Locator("td").First).ToContainTextAsync("1");
        await Assertions.Expect(myRow).ToContainTextAsync("1,000");

        await page.GetByRole(AriaRole.Button, new() { Name = "Town Dashboard" }).ClickAsync();
        await Assertions.Expect(card).ToContainTextAsync("you're #1");
    });
}
