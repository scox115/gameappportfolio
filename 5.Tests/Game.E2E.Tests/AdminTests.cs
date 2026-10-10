using Microsoft.Playwright;

namespace Game.E2E.Tests;

// The admin tools: an admin suspends a cheater, who is signed out at once and told why at the next
// sign-in, corrects their gold, reinstates them, and every step shows in the audit log.
public class AdminTests(ArenaFixture arena) : BrowserTest(arena)
{
    [Fact]
    public Task AnAdmin_CanSuspendCorrectAndReinstateAPlayer() => WithScreenshotsOnFailureAsync(async () =>
    {
        var cheaterName = NewHeroName("Cheat");
        var cheater = await CreateHeroAsync(cheaterName);
        var admin = await CreateAdminAsync(ApiHost.AdminNames[0]);

        await admin.GetByRole(AriaRole.Link, new() { Name = "Admin tools" }).ClickAsync();
        await Assertions.Expect(admin.GetByRole(AriaRole.Heading, new() { Name = "Admin tools", Level = 1 })).ToBeVisibleAsync();
        await admin.GetByLabel("Part of a hero's name").FillAsync(cheaterName[..8]);
        await admin.GetByRole(AriaRole.Button, new() { Name = "Search" }).ClickAsync();
        await admin.GetByRole(AriaRole.Button, new() { Name = cheaterName }).ClickAsync();
        await Assertions.Expect(admin.GetByRole(AriaRole.Heading, new() { Name = cheaterName })).ToBeVisibleAsync();

        // Suspend for a day: the cheater's open browser is signed out straight away.
        await admin.GetByLabel("How long").SelectOptionAsync("1 day");
        await admin.GetByLabel("Reason (the player sees it)").FillAsync("Cheating in duels.");
        await admin.GetByRole(AriaRole.Button, new() { Name = "Suspend", Exact = true }).ClickAsync();
        await Assertions.Expect(admin.GetByRole(AriaRole.Status)).ToHaveTextAsync($"{cheaterName} is suspended and was signed out.");
        await Assertions.Expect(admin.GetByText("Suspended until")).ToBeVisibleAsync();
        await Assertions.Expect(cheater.GetByText("Your hero was suspended by an admin. Sign in again to see why.")).ToBeVisibleAsync();

        // Signing in again says until when and why.
        await cheater.GetByPlaceholder("Enter Character name...").FillAsync(cheaterName);
        await cheater.GetByPlaceholder("At least 8 characters...").FillAsync(Password);
        await cheater.GetByRole(AriaRole.Button, new() { Name = "Play Game" }).ClickAsync();
        await Assertions.Expect(cheater.GetByText("This hero is suspended until")).ToBeVisibleAsync();
        await Assertions.Expect(cheater.GetByText("Reason: Cheating in duels.")).ToBeVisibleAsync();

        // Take back gold won by cheating, then lift the suspension after an appeal.
        await admin.GetByLabel("Gold to add (use a minus sign to take away)").FillAsync("-150");
        await admin.GetByLabel("Reason", new() { Exact = true }).FillAsync("Gold won by cheating.");
        await admin.GetByRole(AriaRole.Button, new() { Name = "Apply" }).ClickAsync();
        await Assertions.Expect(admin.GetByRole(AriaRole.Status)).ToHaveTextAsync($"{cheaterName}'s gold was corrected by -150.");
        await Assertions.Expect(admin.GetByText("-150 gold (200 → 50).").First).ToBeVisibleAsync();

        await admin.GetByLabel("Reason (the player sees it)").FillAsync("Appeal accepted.");
        await admin.GetByRole(AriaRole.Button, new() { Name = "Reinstate" }).ClickAsync();
        await Assertions.Expect(admin.GetByRole(AriaRole.Status)).ToHaveTextAsync($"{cheaterName} can sign in again.");

        // The audit log has every step, newest first.
        var log = admin.Locator("section", new() { Has = admin.GetByRole(AriaRole.Heading, new() { Name = "Audit log" }) });
        var entries = log.Locator(".audit-what");
        await Assertions.Expect(entries.Nth(0)).ToContainTextAsync($"Reinstated {cheaterName}");
        await Assertions.Expect(entries.Nth(1)).ToContainTextAsync($"Corrected gold of {cheaterName}");
        await Assertions.Expect(entries.Nth(2)).ToContainTextAsync($"Suspended {cheaterName}");

        await cheater.GetByRole(AriaRole.Button, new() { Name = "Play Game" }).ClickAsync();
        await Assertions.Expect(cheater.GetByRole(AriaRole.Button, new() { Name = "LAUNCH BATTLE ARENA" })).ToBeVisibleAsync();
        Assert.Empty(await admin.EvaluateAsync<string[]>("() => window.__cspViolations ?? []"));
    });

    [Fact]
    public Task PlayersDontSeeTheAdminTools() => WithScreenshotsOnFailureAsync(async () =>
    {
        var page = await CreateHeroAsync(NewHeroName("Plain"));
        await Assertions.Expect(page.GetByRole(AriaRole.Link, new() { Name = "Admin tools" })).ToHaveCountAsync(0);

        await page.GotoAsync($"{Arena.ClientUrl}/admin");
        await Assertions.Expect(page.GetByText("These tools are for admins.")).ToBeVisibleAsync();
    });

    [Fact]
    public Task AnAdminWithoutTwoFactor_IsToldToTurnItOn() => WithScreenshotsOnFailureAsync(async () =>
    {
        var admin = await CreateHeroAsync(ApiHost.AdminNames[3]);

        await admin.GetByRole(AriaRole.Link, new() { Name = "Admin tools" }).ClickAsync();

        await Assertions.Expect(admin.GetByRole(AriaRole.Heading, new() { Name = "Turn on two-factor sign-in first" })).ToBeVisibleAsync();
        await Assertions.Expect(admin.GetByRole(AriaRole.Heading, new() { Name = "Reports" })).ToHaveCountAsync(0);
    });
}
