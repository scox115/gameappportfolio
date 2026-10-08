using Microsoft.Playwright;

namespace Game.E2E.Tests;

// Every password box has a button to show what was typed, and warns while Caps Lock is on.
public class PasswordFieldTests(ArenaFixture arena) : BrowserTest(arena)
{
    [Fact]
    public Task ThePassword_CanBeShown_AndCapsLockIsPointedOut() => WithScreenshotsOnFailureAsync(async () =>
    {
        var page = await NewBrowserAsync();
        await page.GotoAsync(Arena.ClientUrl);
        var password = page.GetByLabel("Password:", new() { Exact = true });
        var show = page.GetByRole(AriaRole.Button, new() { Name = "Show password" });

        await password.FillAsync("Secret-123");
        await Assertions.Expect(password).ToHaveAttributeAsync("type", "password");

        await show.ClickAsync();
        await Assertions.Expect(password).ToHaveAttributeAsync("type", "text");
        await Assertions.Expect(password).ToHaveValueAsync("Secret-123");
        var hide = page.GetByRole(AriaRole.Button, new() { Name = "Hide password" });
        await Assertions.Expect(hide).ToHaveAttributeAsync("aria-pressed", "true");

        await hide.ClickAsync();
        await Assertions.Expect(password).ToHaveAttributeAsync("type", "password");

        // A capital letter typed without Shift means Caps Lock is on; a small one means it's off again.
        var warning = page.GetByText("Caps Lock is on");
        await password.FocusAsync();
        await page.Keyboard.PressAsync("A");
        await Assertions.Expect(warning).ToBeVisibleAsync();
        await page.Keyboard.PressAsync("b");
        await Assertions.Expect(warning).ToHaveCountAsync(0);
        await page.Keyboard.PressAsync("Shift+C");
        await Assertions.Expect(warning).ToHaveCountAsync(0);
    });
}
