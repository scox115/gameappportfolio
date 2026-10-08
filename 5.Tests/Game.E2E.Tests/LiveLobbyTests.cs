using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace Game.E2E.Tests;

// The live lobby: numbers that change as other players come, wait and duel, and leaderboards that
// reload when a match moves them (docs/adr/0034-live-lobby.md).
public partial class LiveLobbyTests(ArenaFixture arena) : BrowserTest(arena)
{
    [Fact]
    public Task AVisitor_SeesOtherPlayersArriveWaitAndDuel() => WithScreenshotsOnFailureAsync(async () =>
    {
        var visitor = await NewBrowserAsync(); // not signed in
        await visitor.GotoAsync(Arena.ClientUrl);
        var pulse = visitor.Locator(".live-pulse");
        await Assertions.Expect(pulse).ToBeVisibleAsync();
        var before = await ReadAsync(pulse);

        var player = await CreateHeroAsync(NewHeroName("Live"));
        await Assertions.Expect(pulse).ToContainTextAsync(new Regex($@"\b{before.Online + 1} heroe?s? online"));

        await player.GetByRole(AriaRole.Button, new() { Name = "FIND AN OPPONENT" }).ClickAsync();
        await player.Locator("button[aria-label=\"Friendly duel\"]").ClickAsync();
        await Assertions.Expect(pulse).ToContainTextAsync(new Regex($@"\b{before.Waiting + 1} waiting for a duel"));

        // The player keeps the live numbers in view while waiting in the PvP lobby, not just the visitor.
        var playersPulse = player.Locator(".live-pulse");
        await Assertions.Expect(playersPulse).ToBeVisibleAsync();
        await Assertions.Expect(playersPulse).ToContainTextAsync(new Regex(@"\b[1-9]\d* waiting for a duel"));

        await player.GetByRole(AriaRole.Button, new() { Name = "Practice against the Arena Bot" }).ClickAsync();
        await Assertions.Expect(pulse).ToContainTextAsync(new Regex($@"\b{before.Waiting} waiting for a duel"));
        await Assertions.Expect(pulse).ToContainTextAsync(new Regex($@"\b{before.Duels + 1} duels? under way"));
        await Assertions.Expect(playersPulse).ToHaveCountAsync(0); // out of the way once the duel starts
    });

    [Fact]
    public Task OnANarrowWindow_TheHeaderWrapsInsteadOfRunningOffTheScreen() => WithScreenshotsOnFailureAsync(async () =>
    {
        var player = await CreateHeroAsync(NewHeroName("Narrow"));
        await player.SetViewportSizeAsync(800, 900);
        await Assertions.Expect(player.Locator(".live-pulse")).ToBeVisibleAsync();

        var overflow = await player.EvaluateAsync<int>("document.documentElement.scrollWidth - document.documentElement.clientWidth");
        Assert.True(overflow <= 0, $"The page is {overflow}px wider than the window.");
        await Assertions.Expect(player.GetByRole(AriaRole.Button, new() { Name = "Logout" })).ToBeInViewportAsync();
    });

    [Fact]
    public Task AnOpenLeaderboard_ReloadsWhenAMatchMovesIt() => WithScreenshotsOnFailureAsync(async () =>
    {
        var watcher = await CreateHeroAsync(NewHeroName("Watch"));
        await watcher.GetByRole(AriaRole.Button, new() { Name = "Leaderboards" }).ClickAsync();
        await Assertions.Expect(watcher.Locator(".live-pulse")).ToBeVisibleAsync();

        var climber = NewHeroName("Climb");
        await CreateHeroAsync(climber);
        var climbersRow = watcher.Locator("tbody tr", new() { HasText = climber });
        await Assertions.Expect(climbersRow.GetByText("9500")).ToHaveCountAsync(0);

        await Arena.RecordMatchAsync(climber, 9_500);

        // No click: the page reloads the rankings by itself.
        await Assertions.Expect(climbersRow).ToContainTextAsync("9500");
        await Assertions.Expect(watcher.GetByText("The rankings were just updated.")).ToBeAttachedAsync();
    });

    private static async Task<(int Online, int Waiting, int Duels)> ReadAsync(ILocator pulse)
    {
        var text = await pulse.InnerTextAsync();
        int Number(Regex pattern) => int.Parse(pattern.Match(text).Groups[1].Value);
        return (Number(OnlinePattern()), Number(WaitingPattern()), Number(DuelsPattern()));
    }

    [GeneratedRegex(@"(\d+) heroe?s? online")]
    private static partial Regex OnlinePattern();

    [GeneratedRegex(@"(\d+) waiting for a duel")]
    private static partial Regex WaitingPattern();

    [GeneratedRegex(@"(\d+) duels? under way")]
    private static partial Regex DuelsPattern();
}
