using Microsoft.Playwright;

namespace Game.E2E.Tests;

// Account recovery: a player adds a recovery email and confirms it from the emailed link, forgets
// their password, and chooses a new one from the reset link.
public class RecoveryTests(ArenaFixture arena) : BrowserTest(arena)
{
    private const string NewPassword = "Brand-New-Pass2";

    [Fact]
    public Task APlayer_CanAddAnEmail_AndResetAForgottenPassword() => WithScreenshotsOnFailureAsync(async () =>
    {
        var name = NewHeroName("Forget");
        var address = $"{name.ToLowerInvariant()}@example.com";
        var page = await CreateHeroAsync(name);

        // Add the address; it's only used once the emailed link is clicked.
        await page.GetByRole(AriaRole.Button, new() { Name = "Your account and data" }).ClickAsync();
        var dialog = page.GetByRole(AriaRole.Dialog, new() { Name = "Your account" });
        await dialog.GetByLabel("Email address").FillAsync(address);
        await dialog.GetByLabel("Your password", new() { Exact = true }).FillAsync(Password);
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Send confirmation link" }).ClickAsync();
        await Assertions.Expect(dialog.GetByRole(AriaRole.Status)).ToHaveTextAsync($"Check {address} for a confirmation link.");
        await Assertions.Expect(dialog.GetByText("We sent a confirmation link to")).ToBeVisibleAsync();

        var confirmLink = await Arena.Emails.LatestLinkToAsync(address);
        var mailTab = await page.Context.NewPageAsync();
        await mailTab.GotoAsync(confirmLink);
        await Assertions.Expect(mailTab.GetByRole(AriaRole.Status)).ToContainTextAsync($"{address} is confirmed");
        // The one-time token doesn't linger in the address bar.
        Assert.DoesNotContain("token=", mailTab.Url);

        // Later, on another computer, the password is forgotten.
        var later = await NewBrowserAsync();
        await later.GotoAsync(Arena.ClientUrl);
        await later.GetByRole(AriaRole.Link, new() { Name = "Forgot your password?" }).ClickAsync();
        await later.GetByLabel("Hero name").FillAsync(name);
        await later.GetByRole(AriaRole.Button, new() { Name = "Send me a link" }).ClickAsync();
        await Assertions.Expect(later.GetByRole(AriaRole.Status)).ToContainTextAsync("a link to choose a new password is on its way");

        var resetLink = await Arena.Emails.LatestLinkToAsync(address);
        Assert.Contains("/reset-password?", resetLink);
        await later.GotoAsync(resetLink);
        await later.GetByLabel("New password (at least 8 characters)").FillAsync(NewPassword);
        await later.GetByLabel("The same password again").FillAsync(NewPassword);
        await later.GetByRole(AriaRole.Button, new() { Name = "Change my password" }).ClickAsync();
        await Assertions.Expect(later.GetByRole(AriaRole.Status)).ToContainTextAsync("Your password was changed");

        // The first browser was signed out, and only the new password works.
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Play Game" })).ToBeVisibleAsync();
        await later.GetByRole(AriaRole.Link, new() { Name = "Sign in" }).ClickAsync();
        await later.GetByPlaceholder("Enter Character name...").FillAsync(name);
        await later.GetByPlaceholder("At least 8 characters...").FillAsync(NewPassword);
        await later.GetByRole(AriaRole.Button, new() { Name = "Play Game" }).ClickAsync();
        await Assertions.Expect(later.GetByRole(AriaRole.Button, new() { Name = "LAUNCH BATTLE ARENA" })).ToBeVisibleAsync();

        // The used link doesn't work twice.
        var replay = await NewBrowserAsync();
        await replay.GotoAsync(resetLink);
        await replay.GetByLabel("New password (at least 8 characters)").FillAsync("Third-Pass-33");
        await replay.GetByLabel("The same password again").FillAsync("Third-Pass-33");
        await replay.GetByRole(AriaRole.Button, new() { Name = "Change my password" }).ClickAsync();
        await Assertions.Expect(replay.GetByRole(AriaRole.Alert)).ToContainTextAsync("expired or was already used");
        Assert.Empty(await later.EvaluateAsync<string[]>("() => window.__cspViolations ?? []"));
    });

    [Fact]
    public Task AnEmailGivenWhenCreatingTheHero_GetsAConfirmationLink() => WithScreenshotsOnFailureAsync(async () =>
    {
        var name = NewHeroName("Signup");
        var address = $"{name.ToLowerInvariant()}@example.com";
        var page = await NewBrowserAsync();
        await page.GotoAsync(Arena.ClientUrl);
        await page.GetByRole(AriaRole.Button, new() { Name = "Create a new hero" }).ClickAsync();
        await page.GetByPlaceholder("Enter Character name...").FillAsync(name);
        await page.GetByPlaceholder("At least 8 characters...").FillAsync(Password);
        await page.GetByLabel("Recovery email (optional)").FillAsync(address);
        await page.GetByRole(AriaRole.Button, new() { Name = "Create Hero" }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "LAUNCH BATTLE ARENA" })).ToBeVisibleAsync();

        Assert.Contains("/confirm-email?", await Arena.Emails.LatestLinkToAsync(address));
        // The address is on its way to being confirmed, so there's nothing to remind about.
        await page.GetByRole(AriaRole.Button, new() { Name = "Your account and data" }).ClickAsync();
        await Assertions.Expect(page.GetByText("We sent a confirmation link to")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByText("Add a recovery email so you can reset")).ToBeHiddenAsync();
    });

    [Fact]
    public Task AHeroWithNoEmail_IsReminded_AWeekAfterNotNow_OrNeverIfTheySaySo() => WithScreenshotsOnFailureAsync(async () =>
    {
        var name = NewHeroName("Remind");
        var page = await CreateHeroAsync(name);
        var reminder = page.GetByRole(AriaRole.Note).Filter(new() { HasText = "Add a recovery email" });

        await Assertions.Expect(reminder).ToBeVisibleAsync();
        await reminder.GetByRole(AriaRole.Button, new() { Name = "Add one" }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Dialog, new() { Name = "Your account" }).GetByLabel("Email address")).ToBeVisibleAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Close" }).ClickAsync();

        await reminder.GetByRole(AriaRole.Button, new() { Name = "Not now" }).ClickAsync();
        await Assertions.Expect(reminder).ToBeHiddenAsync();
        // Signing in again in the same browser, it stays put off.
        await SignInAgainAsync(page, name);
        await Assertions.Expect(reminder).ToBeHiddenAsync();

        // A week later it's back.
        var key = await page.EvaluateAsync<string>("() => Object.keys(localStorage).find(k => k.startsWith('recovery-email-reminder'))");
        await page.EvaluateAsync("k => localStorage.setItem(k, new Date(Date.now() - 1000).toISOString())", key);
        await SignInAgainAsync(page, name);
        await Assertions.Expect(reminder).ToBeVisibleAsync();

        // "Don't remind me again" puts it away for good.
        await reminder.GetByLabel("Don't remind me again").CheckAsync();
        await reminder.GetByRole(AriaRole.Button, new() { Name = "Not now" }).ClickAsync();
        await Assertions.Expect(reminder).ToBeHiddenAsync();
        Assert.Equal("never", await page.EvaluateAsync<string>("k => localStorage.getItem(k)", key));
        await SignInAgainAsync(page, name);
        await Assertions.Expect(reminder).ToBeHiddenAsync();
    });

    private static async Task SignInAgainAsync(IPage page, string name)
    {
        await page.GetByRole(AriaRole.Button, new() { Name = "Logout" }).ClickAsync();
        await page.GetByPlaceholder("Enter Character name...").FillAsync(name);
        await page.GetByPlaceholder("At least 8 characters...").FillAsync(Password);
        await page.GetByRole(AriaRole.Button, new() { Name = "Play Game" }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Your account and data" })).ToBeVisibleAsync();
    }

    [Fact]
    public Task AMangledResetLink_SaysItIsIncomplete_InsteadOfCrashing() => WithScreenshotsOnFailureAsync(async () =>
    {
        var page = await NewBrowserAsync();
        await page.GotoAsync($"{Arena.ClientUrl}/reset-password?user=not-a-guid&token=abc");
        await Assertions.Expect(page.GetByRole(AriaRole.Alert)).ToContainTextAsync("This link is incomplete");
        await Assertions.Expect(page.Locator("#blazor-error-ui")).ToBeHiddenAsync();
    });
}
