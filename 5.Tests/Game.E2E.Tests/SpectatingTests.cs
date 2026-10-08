using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace Game.E2E.Tests;

// Anyone can open the list of duels under way from the live numbers and watch one turn by turn
// (docs/adr/0036-spectating.md).
public class SpectatingTests(ArenaFixture arena) : BrowserTest(arena)
{
    [Fact]
    public Task AVisitor_WatchesADuelFromTheFirstMoveToTheResult() => WithScreenshotsOnFailureAsync(async () =>
    {
        var name = NewHeroName("Seen");
        var player = await CreateHeroAsync(name, heroClass: "Sorcerer");
        await player.GetByRole(AriaRole.Button, new() { Name = "FIND AN OPPONENT" }).ClickAsync();
        await player.Locator("button[aria-label=\"Friendly duel\"]").ClickAsync();
        await player.GetByRole(AriaRole.Button, new() { Name = "Practice against the Arena Bot" }).ClickAsync();
        await Assertions.Expect(player.GetByText("Practice duel against the Arena Bot")).ToBeVisibleAsync();

        // A visitor, not signed in, follows the live numbers to the list and picks the player's duel.
        var visitor = await NewBrowserAsync();
        await visitor.GotoAsync(Arena.ClientUrl);
        await visitor.Locator(".live-pulse").GetByRole(AriaRole.Link).ClickAsync();
        await Assertions.Expect(visitor.GetByRole(AriaRole.Heading, new() { Name = "Watch a duel" })).ToBeVisibleAsync();
        var listed = visitor.GetByRole(AriaRole.Listitem).Filter(new() { HasText = name });
        await listed.GetByRole(AriaRole.Link, new() { NameRegex = new Regex("^Watch ") }).ClickAsync();
        await Assertions.Expect(visitor.GetByRole(AriaRole.Heading, new() { NameRegex = new Regex($"{name} vs|vs {name}") })).ToBeVisibleAsync();
        await Assertions.Expect(visitor.GetByText(new Regex("'s turn"))).ToBeVisibleAsync();

        // The player casts Fireball on each of their turns; the server plays the bot's.
        var visitorsLog = visitor.GetByRole(AriaRole.Log);
        var result = player.GetByRole(AriaRole.Heading, new() { NameRegex = new Regex("^(VICTORY|DEFEAT)$") });
        var yourTurn = player.GetByText("Your turn");
        for (var poll = 0; poll < 300 && !await result.IsVisibleAsync(); poll++)
        {
            if (await yourTurn.IsVisibleAsync())
            {
                try
                {
                    await player.GetByRole(AriaRole.Button, new() { NameRegex = new Regex("Fireball") }).Or(result).First.ClickAsync(new() { Timeout = 2_000 });
                }
                catch (TimeoutException)
                {
                }
            }
            await player.WaitForTimeoutAsync(200);
        }

        await Assertions.Expect(result).ToBeVisibleAsync();
        await Assertions.Expect(visitorsLog).ToContainTextAsync($"{name} cast Fireball");
        await Assertions.Expect(visitorsLog).ToContainTextAsync("Arena Bot");
        var winner = await result.InnerTextAsync() == "VICTORY" ? name : "Arena Bot";
        await Assertions.Expect(visitor.GetByRole(AriaRole.Heading, new() { NameRegex = new Regex($"{Regex.Escape(winner)} wins!") })).ToBeVisibleAsync();
    });

    [Fact]
    public Task ALinkToADuelThatIsOver_SaysSo() => WithScreenshotsOnFailureAsync(async () =>
    {
        var visitor = await NewBrowserAsync();
        await visitor.GotoAsync($"{Arena.ClientUrl.TrimEnd('/')}/watch/{Guid.NewGuid()}");

        await Assertions.Expect(visitor.GetByText("That duel isn't in the arena any more.")).ToBeVisibleAsync();
        await visitor.GetByRole(AriaRole.Link, new() { Name = "See the duels under way" }).ClickAsync();
        await Assertions.Expect(visitor.GetByRole(AriaRole.Heading, new() { Name = "Watch a duel" })).ToBeVisibleAsync();
    });
}
