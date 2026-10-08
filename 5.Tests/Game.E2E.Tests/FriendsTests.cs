using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace Game.E2E.Tests;

// Adding a friend, and challenging them to a duel from town (docs/adr/0037-friends-and-challenges.md).
public class FriendsTests(ArenaFixture arena) : BrowserTest(arena)
{
    [Fact]
    public Task TwoHeroesBecomeFriends_AndOneChallengesTheOther() => WithScreenshotsOnFailureAsync(async () =>
    {
        var (alice, aliceName, bob, bobName) = await FriendsAsync();

        await alice.GetByRole(AriaRole.Button, new() { Name = $"Challenge {bobName}" }).ClickAsync();
        await Assertions.Expect(alice.GetByText($"You challenged {bobName}")).ToBeVisibleAsync();

        // Bob is asked on whatever screen he's on, and accepting takes him straight into the duel.
        var invite = bob.GetByRole(AriaRole.Alertdialog);
        await Assertions.Expect(invite).ToContainTextAsync($"{aliceName} challenges you!");
        await invite.GetByRole(AriaRole.Button, new() { Name = "Accept" }).ClickAsync();

        foreach (var (page, opponent) in new[] { (alice, bobName), (bob, aliceName) })
        {
            await Assertions.Expect(page.GetByText(opponent).First).ToBeVisibleAsync();
            await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "🏳️ Forfeit" })).ToBeVisibleAsync();
        }
        await Assertions.Expect(invite).ToHaveCountAsync(0);

        await alice.GetByRole(AriaRole.Button, new() { Name = "🏳️ Forfeit" }).ClickAsync();
        await Assertions.Expect(bob.GetByRole(AriaRole.Heading, new() { Name = "VICTORY" })).ToBeVisibleAsync();
    });

    [Fact]
    public Task AChallengeTurnedDown_TellsTheChallenger() => WithScreenshotsOnFailureAsync(async () =>
    {
        var (alice, _, bob, bobName) = await FriendsAsync();

        await alice.GetByRole(AriaRole.Button, new() { Name = $"Challenge {bobName}" }).ClickAsync();
        await bob.GetByRole(AriaRole.Alertdialog).GetByRole(AriaRole.Button, new() { Name = "Not now" }).ClickAsync();

        await Assertions.Expect(alice.GetByText($"{bobName} can't duel right now.")).ToBeVisibleAsync();
        await Assertions.Expect(alice.Locator("button[aria-label=\"Friendly duel\"]")).ToBeVisibleAsync(); // back to choosing
    });

    // Alice asks, Bob accepts; each list updates without a reload.
    private async Task<(IPage Alice, string AliceName, IPage Bob, string BobName)> FriendsAsync()
    {
        var aliceName = NewHeroName("Pal");
        var bobName = NewHeroName("Pal");
        var alice = await CreateHeroAsync(aliceName);
        var bob = await CreateHeroAsync(bobName);

        await alice.GetByLabel("Hero name").FillAsync(bobName);
        await alice.GetByRole(AriaRole.Button, new() { Name = "➕ Add" }).ClickAsync();
        await Assertions.Expect(alice.GetByText($"Friend request sent to {bobName}.")).ToBeVisibleAsync();

        await bob.GetByRole(AriaRole.Button, new() { Name = $"Accept {aliceName}" }).ClickAsync();
        await Assertions.Expect(bob.GetByText(new Regex($"You and {aliceName} are now friends"))).ToBeVisibleAsync();
        await Assertions.Expect(alice.GetByRole(AriaRole.Button, new() { Name = $"Challenge {bobName}" })).ToBeVisibleAsync();
        return (alice, aliceName, bob, bobName);
    }
}
