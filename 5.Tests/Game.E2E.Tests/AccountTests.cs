using System.Text.Json;
using Microsoft.Playwright;

namespace Game.E2E.Tests;

// "Your account and data" in town: download a copy of the hero's data, then delete the hero.
public class AccountTests(ArenaFixture arena) : BrowserTest(arena)
{
    [Fact]
    public Task APlayer_CanDownloadTheirData_AndDeleteTheirHero() => WithScreenshotsOnFailureAsync(async () =>
    {
        var name = NewHeroName("Bye");
        var page = await CreateHeroAsync(name);
        await page.GetByRole(AriaRole.Button, new() { Name = "Your account and data" }).ClickAsync();
        var dialog = page.GetByRole(AriaRole.Dialog, new() { Name = "Your account" });

        // The download is a JSON file named after the hero, holding their profile.
        var download = await page.RunAndWaitForDownloadAsync(() =>
            dialog.GetByRole(AriaRole.Button, new() { Name = "Download my data" }).ClickAsync());
        Assert.StartsWith($"card-arena-{name}-", download.SuggestedFilename);
        Assert.EndsWith(".json", download.SuggestedFilename);
        await using (var file = await download.CreateReadStreamAsync())
        {
            var export = await JsonDocument.ParseAsync(file);
            Assert.Equal(name, export.RootElement.GetProperty("profile").GetProperty("username").GetString());
        }

        // A wrong password changes nothing.
        var deleteButton = dialog.GetByRole(AriaRole.Button, new() { Name = "Delete my hero forever" });
        await Assertions.Expect(deleteButton).ToBeDisabledAsync();
        await dialog.GetByLabel("Your password, to confirm").FillAsync("Wrong-Pass1");
        await deleteButton.ClickAsync();
        await Assertions.Expect(dialog.GetByRole(AriaRole.Alert)).ToHaveTextAsync("That password isn't right.");

        await dialog.GetByLabel("Your password, to confirm").FillAsync(Password);
        await deleteButton.ClickAsync();

        await Assertions.Expect(page.GetByText("Your hero was deleted. Thanks for playing!")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Play Game" })).ToBeVisibleAsync();

        // The hero is gone: signing in with it no longer works.
        await page.GetByPlaceholder("Enter Character name...").FillAsync(name);
        await page.GetByPlaceholder("At least 8 characters...").FillAsync(Password);
        await page.GetByRole(AriaRole.Button, new() { Name = "Play Game" }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "LAUNCH BATTLE ARENA" })).ToHaveCountAsync(0);
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Play Game" })).ToBeVisibleAsync();
        Assert.Empty(await page.EvaluateAsync<string[]>("() => window.__cspViolations ?? []"));
    });
}
