using Microsoft.Playwright;

namespace Game.E2E.Tests;

// The copyright footer, the terms and privacy pages, and the notice on the create-hero form.
public class LegalPagesTests(ArenaFixture arena) : BrowserTest(arena)
{
    [Fact]
    public Task TheFooter_ShowsTheCopyright_AndOpensTheTermsAndPrivacyPages() => WithScreenshotsOnFailureAsync(async () =>
    {
        var page = await NewBrowserAsync();
        await page.GotoAsync(Arena.ClientUrl);

        var footer = page.GetByRole(AriaRole.Contentinfo);
        await Assertions.Expect(footer).ToContainTextAsync("© 2026 Scott Cox");

        await footer.GetByRole(AriaRole.Link, new() { Name = "Terms" }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Terms of Service", Level = 1 })).ToBeVisibleAsync();
        await Assertions.Expect(page).ToHaveTitleAsync("Terms of Service · Kings of the Card Arena");

        await page.GetByRole(AriaRole.Contentinfo).GetByRole(AriaRole.Link, new() { Name = "Privacy" }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Privacy Policy", Level = 1 })).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByText("Your account and data").First).ToBeVisibleAsync();

        await page.GetByRole(AriaRole.Link, new() { Name = "Back to the game" }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Play Game" })).ToBeVisibleAsync();
        Assert.Empty(await page.EvaluateAsync<string[]>("() => window.__cspViolations ?? []"));
    });

    [Fact]
    public Task CreatingAHero_LinksToTheTermsAndPrivacyPolicy() => WithScreenshotsOnFailureAsync(async () =>
    {
        var page = await NewBrowserAsync();
        await page.GotoAsync(Arena.ClientUrl);
        await page.GetByRole(AriaRole.Button, new() { Name = "Create a new hero" }).ClickAsync();

        var notice = page.GetByText("By creating a hero you agree to the");
        await Assertions.Expect(notice).ToBeVisibleAsync();
        await notice.GetByRole(AriaRole.Link, new() { Name = "Privacy Policy" }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Privacy Policy", Level = 1 })).ToBeVisibleAsync();
    });
}
