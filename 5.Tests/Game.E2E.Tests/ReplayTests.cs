using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace Game.E2E.Tests;

// A finished duel can be played back card by card (docs/adr/0038-match-replays.md).
public class ReplayTests(ArenaFixture arena) : BrowserTest(arena)
{
    [Fact]
    public Task AFinishedDuel_CanBeReplayedAndSteppedThrough() => WithScreenshotsOnFailureAsync(async () =>
    {
        var name = NewHeroName("Again");
        var page = await CreateHeroAsync(name, heroClass: "Sorcerer");
        await page.GetByRole(AriaRole.Button, new() { Name = "FIND AN OPPONENT" }).ClickAsync();
        await page.Locator("button[aria-label=\"Friendly duel\"]").ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Practice against the Arena Bot" }).ClickAsync();

        // A few Fireballs, then forfeit, so the replay has cards from both sides.
        var yourTurn = page.GetByText("Your turn");
        var fireball = page.GetByRole(AriaRole.Button, new() { NameRegex = new Regex("Fireball") });
        for (var cast = 0; cast < 3; cast++)
        {
            await Assertions.Expect(yourTurn).ToBeVisibleAsync();
            await fireball.ClickAsync();
            await Assertions.Expect(page.GetByText(new Regex($"Turn {cast * 2 + 2}: "))).ToBeVisibleAsync(); // the bot's answer
        }
        await Assertions.Expect(yourTurn).ToBeVisibleAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "🏳️ Forfeit" }).ClickAsync();

        await page.GetByRole(AriaRole.Link, new() { Name = "▶ Watch the replay" }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { NameRegex = new Regex($"Replay: {name} vs") })).ToBeVisibleAsync();

        // It plays by itself to the result.
        var position = page.GetByText(new Regex(@"^Card \d+ of 6$"));
        await Assertions.Expect(position).ToHaveTextAsync("Card 6 of 6", new() { Timeout = 15_000 });
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { NameRegex = new Regex("Arena Bot wins!") })).ToBeVisibleAsync();
        var log = page.GetByRole(AriaRole.Log);
        await Assertions.Expect(log).ToContainTextAsync($"{name} forfeited");

        // Back to the start, then one card at a time.
        await page.GetByRole(AriaRole.Button, new() { Name = "Back to the start" }).ClickAsync();
        await Assertions.Expect(position).ToHaveTextAsync("Card 0 of 6");
        await Assertions.Expect(log).Not.ToContainTextAsync("Turn 1:");
        await page.GetByRole(AriaRole.Button, new() { Name = "Next card" }).ClickAsync();
        await Assertions.Expect(position).ToHaveTextAsync("Card 1 of 6");
        await Assertions.Expect(log).ToContainTextAsync($"Turn 1: {name} cast Fireball");
        await Assertions.Expect(page.GetByText("· ⚡ played")).ToHaveCountAsync(1);
    });
}
