using Game.Core.Security;
using Microsoft.Playwright;

namespace Game.E2E.Tests;

// A player turns on two-factor sign-in with an authenticator app, then signs in with a code from it
// and, on another browser, with a recovery code.
public class TwoFactorTests(ArenaFixture arena) : BrowserTest(arena)
{
    [Fact]
    public Task APlayer_TurnsOnTwoFactor_AndSignsInWithACode() => WithScreenshotsOnFailureAsync(async () =>
    {
        var name = NewHeroName("Safe");
        var page = await CreateHeroAsync(name);

        // Set it up from the account dialog.
        await page.GetByRole(AriaRole.Button, new() { Name = "Your account and data" }).ClickAsync();
        var dialog = page.GetByRole(AriaRole.Dialog, new() { Name = "Your account" });
        await dialog.Locator("#two-factor-password").FillAsync(Password);
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Set up two-factor sign-in" }).ClickAsync();
        await Assertions.Expect(dialog.GetByRole(AriaRole.Img, new() { Name = "QR code for your authenticator app" })).ToBeVisibleAsync();
        await AccessibilityTests.AssertNoViolationsAsync(page, "two-factor setup");

        var key = Base32.Decode(await dialog.Locator("code.two-factor-key").InnerTextAsync());
        await dialog.GetByLabel("6-digit code from the app").FillAsync(Totp.Code(key, Totp.StepAt(DateTimeOffset.UtcNow)));
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Turn on" }).ClickAsync();

        var codes = dialog.GetByRole(AriaRole.List, new() { Name = "Recovery codes" }).GetByRole(AriaRole.Listitem);
        await Assertions.Expect(codes).ToHaveCountAsync(10);
        var recoveryCode = await codes.First.InnerTextAsync();
        await dialog.GetByRole(AriaRole.Button, new() { Name = "I've saved them" }).ClickAsync();
        await Assertions.Expect(dialog.GetByText("You have 10 recovery codes left.")).ToBeVisibleAsync();

        // Signing in now asks for a code once the password is right.
        var again = await NewBrowserAsync();
        await again.GotoAsync(Arena.ClientUrl);
        await again.GetByPlaceholder("Enter Character name...").FillAsync(name);
        await again.GetByPlaceholder("At least 8 characters...").FillAsync(Password);
        await again.GetByRole(AriaRole.Button, new() { Name = "Play Game" }).ClickAsync();
        var codeBox = again.GetByLabel("Code from your authenticator app:");
        await Assertions.Expect(codeBox).ToBeFocusedAsync();
        await AccessibilityTests.AssertNoViolationsAsync(again, "sign-in code");

        await codeBox.FillAsync("000000");
        await again.GetByRole(AriaRole.Button, new() { Name = "Play Game" }).ClickAsync();
        await Assertions.Expect(again.GetByText("That code isn't right.")).ToBeVisibleAsync();

        // The setup code was used, so take the next one (accepted early, for clocks that are a little off).
        await codeBox.FillAsync(Totp.Code(key, Totp.StepAt(DateTimeOffset.UtcNow) + 1));
        await again.GetByRole(AriaRole.Button, new() { Name = "Play Game" }).ClickAsync();
        await Assertions.Expect(again.GetByRole(AriaRole.Button, new() { Name = "LAUNCH BATTLE ARENA" })).ToBeVisibleAsync();

        // A recovery code works in place of the app.
        var lostPhone = await NewBrowserAsync();
        await lostPhone.GotoAsync(Arena.ClientUrl);
        await lostPhone.GetByPlaceholder("Enter Character name...").FillAsync(name);
        await lostPhone.GetByPlaceholder("At least 8 characters...").FillAsync(Password);
        await lostPhone.GetByRole(AriaRole.Button, new() { Name = "Play Game" }).ClickAsync();
        await lostPhone.GetByLabel("Code from your authenticator app:").FillAsync(recoveryCode);
        await lostPhone.GetByRole(AriaRole.Button, new() { Name = "Play Game" }).ClickAsync();
        await Assertions.Expect(lostPhone.GetByRole(AriaRole.Button, new() { Name = "LAUNCH BATTLE ARENA" })).ToBeVisibleAsync();
    });
}
