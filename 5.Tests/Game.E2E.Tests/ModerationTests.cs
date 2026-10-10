using Microsoft.Playwright;

namespace Game.E2E.Tests;

// A player reports an offensive name from the leaderboard, and an admin renames the hero from the
// report queue.
public class ModerationTests(ArenaFixture arena) : BrowserTest(arena)
{
    [Fact]
    public Task AReportedName_CanBeRenamedByAnAdmin() => WithScreenshotsOnFailureAsync(async () =>
    {
        var rudeName = NewHeroName("Rude");
        await CreateHeroAsync(rudeName);
        await Arena.SetRatingAsync(rudeName, 9_000); // top of the leaderboard
        var reporter = await CreateHeroAsync(NewHeroName("Fair"));

        // Report the name from the leaderboard.
        await reporter.GetByRole(AriaRole.Button, new() { Name = "Leaderboards" }).ClickAsync();
        await reporter.GetByRole(AriaRole.Button, new() { Name = $"Report {rudeName}" }).ClickAsync();
        var dialog = reporter.GetByRole(AriaRole.Dialog, new() { Name = $"Report {rudeName}" });
        await Assertions.Expect(dialog.GetByLabel("Their name")).ToBeCheckedAsync();
        await Assertions.Expect(dialog.GetByLabel("Their portrait")).ToHaveCountAsync(0); // no portrait uploaded
        await dialog.GetByLabel("Anything an admin should know? (optional)").FillAsync("It's a slur in my language.");
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Send report" }).ClickAsync();
        await Assertions.Expect(dialog.GetByRole(AriaRole.Status)).ToContainTextAsync($"An admin will look at {rudeName}'s name.");
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Done" }).ClickAsync();
        await Assertions.Expect(dialog).ToHaveCountAsync(0);

        // The admin finds it in the report queue and renames the hero.
        var admin = await CreateAdminAsync(ApiHost.AdminNames[2]);
        await admin.GetByRole(AriaRole.Link, new() { Name = "Admin tools" }).ClickAsync();
        var queue = admin.Locator("section", new() { Has = admin.GetByRole(AriaRole.Heading, new() { Name = "Reports" }) });
        await Assertions.Expect(queue.GetByText("“It's a slur in my language.”")).ToBeVisibleAsync();
        await queue.GetByRole(AriaRole.Button, new() { Name = rudeName }).ClickAsync();
        await Assertions.Expect(admin.GetByText("Waiting for an answer: 1 name report.")).ToBeVisibleAsync();

        var newName = NewHeroName("Kind");
        await admin.GetByLabel("New name (they can still sign in with the old one)").FillAsync(newName);
        await admin.GetByLabel("Reason for the rename").FillAsync("Reported as a slur.");
        await admin.GetByRole(AriaRole.Button, new() { Name = "Rename", Exact = true }).ClickAsync();
        await Assertions.Expect(admin.GetByRole(AriaRole.Status)).ToHaveTextAsync($"{rudeName} is now called {newName}.");
        await Assertions.Expect(queue.GetByText("Nothing is waiting.")).ToBeVisibleAsync();

        // The hero still signs in with the old name, and plays under the new one.
        var renamed = await SignInAsync(rudeName);
        await Assertions.Expect(renamed.GetByText(newName).First).ToBeVisibleAsync();
    });
}
