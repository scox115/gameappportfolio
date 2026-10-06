using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace Game.E2E.Tests;

// Plays the game the way a person does: in Chromium, against the real client and API.
public class GameFlowTests(ArenaFixture arena) : BrowserTest(arena)
{
    private static readonly Regex Result = new("^(VICTORY|DEFEAT)$");

    [Fact]
    public Task ANewHero_ArrivesInTown() => WithScreenshotsOnFailureAsync(async () =>
    {
        var name = NewHeroName("Town");

        var page = await CreateHeroAsync(name);

        await Assertions.Expect(page.GetByText(name).First).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "GOLD SHOP" })).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "FIND AN OPPONENT" })).ToBeVisibleAsync();
    });

    [Fact]
    public Task AHero_CanSignInAgain() => WithScreenshotsOnFailureAsync(async () =>
    {
        var name = NewHeroName("Back");
        var first = await CreateHeroAsync(name);
        await first.Context.CloseAsync();

        var page = await SignInAsync(name);

        await Assertions.Expect(page.GetByText(name).First).ToBeVisibleAsync();
    });

    [Fact]
    public Task CastingFireball_BeatsTheBoss() => WithScreenshotsOnFailureAsync(async () =>
    {
        var page = await CreateHeroAsync(NewHeroName("Boss"), heroClass: "Sorcerer");

        await page.GetByRole(AriaRole.Button, new() { Name = "LAUNCH BATTLE ARENA" }).ClickAsync();
        await Assertions.Expect(page.GetByText("Next boss move")).ToBeVisibleAsync();

        // Dragon Claw whenever it is ready, Fireball while it recharges (Fireball never does).
        // The page ignores clicks while a turn is in flight, and clicking "the card or the result"
        // keeps the loop from waiting on a card that just disappeared.
        var heading = page.GetByRole(AriaRole.Heading, new() { NameRegex = Result });
        var dragonClaw = page.GetByRole(AriaRole.Button, new() { NameRegex = new Regex("Dragon Claw") });
        for (var click = 0; click < 100 && !await heading.IsVisibleAsync(); click++)
        {
            var card = await IsReadyAsync(dragonClaw) ? dragonClaw : Fireball(page);
            await TryClickAsync(card.Or(heading).First);
            await page.WaitForTimeoutAsync(200);
        }

        await Assertions.Expect(heading).ToHaveTextAsync("VICTORY");
        await page.GetByRole(AriaRole.Button, new() { Name = "Return to Town" }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "LAUNCH BATTLE ARENA" })).ToBeVisibleAsync();
    });

    [Fact]
    public Task AFriendlyDuel_HasOneWinnerAndOneLoser() => WithScreenshotsOnFailureAsync(async () =>
    {
        var alice = await CreateHeroAsync(NewHeroName("Duel"));
        var bob = await CreateHeroAsync(NewHeroName("Duel"), heroClass: "Paladin");

        foreach (var page in new[] { alice, bob })
        {
            await page.GetByRole(AriaRole.Button, new() { Name = "FIND AN OPPONENT" }).ClickAsync();
            await page.Locator("button[aria-label=\"Friendly duel\"]").ClickAsync();
        }

        var players = new[] { alice, bob };
        var headings = players.Select(p => p.GetByRole(AriaRole.Heading, new() { NameRegex = Result })).ToArray();
        var yourTurn = players.Select(p => p.GetByText("Your turn")).ToArray();
        for (var poll = 0; poll < 300; poll++)
        {
            if (await headings[0].IsVisibleAsync() && await headings[1].IsVisibleAsync()) break;

            // Whoever's turn it is casts Fireball (the result, if the duel just ended).
            for (var i = 0; i < players.Length; i++)
            {
                if (await yourTurn[i].IsVisibleAsync())
                {
                    await TryClickAsync(Fireball(players[i]).Or(headings[i]).First);
                }
            }

            await alice.WaitForTimeoutAsync(200);
        }

        await Assertions.Expect(headings[0]).ToBeVisibleAsync();
        await Assertions.Expect(headings[1]).ToBeVisibleAsync();
        var results = new[] { await headings[0].InnerTextAsync(), await headings[1].InnerTextAsync() };
        Assert.Equal(["DEFEAT", "VICTORY"], results.Order());
    });

    [Fact]
    public Task SigningInOnAnotherBrowser_SignsTheFirstOneOut() => WithScreenshotsOnFailureAsync(async () =>
    {
        var name = NewHeroName("Solo");
        var first = await CreateHeroAsync(name);

        // Give the first browser's session connection a moment to open before the second sign-in.
        await first.WaitForTimeoutAsync(1_500);
        var second = await SignInAsync(name);

        await Assertions.Expect(first.GetByText("signed in on another browser")).ToBeVisibleAsync();
        await second.GetByRole(AriaRole.Button, new() { Name = "LAUNCH BATTLE ARENA" }).ClickAsync();
        await Assertions.Expect(second.GetByText("Next boss move")).ToBeVisibleAsync();
    });

    // The card can turn disabled (or disappear) between seeing it and clicking it; just try again later.
    private static async Task TryClickAsync(ILocator target)
    {
        try
        {
            await target.ClickAsync(new() { Timeout = 2_000 });
        }
        catch (TimeoutException)
        {
        }
    }

    // False when the card is recharging or gone (the battle just ended).
    private static async Task<bool> IsReadyAsync(ILocator card)
    {
        try
        {
            return await card.IsEnabledAsync(new() { Timeout = 2_000 });
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    private static ILocator Fireball(IPage page) =>
        page.GetByRole(AriaRole.Button, new() { NameRegex = new Regex("Fireball") });
}
