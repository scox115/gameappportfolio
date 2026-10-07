using Microsoft.Playwright;

namespace Game.E2E.Tests;

// The strip that says the game server is waking up (wwwroot/js/wake.js and Layout/ServerWake.razor).
public class ServerWakeTests(ArenaFixture arena) : BrowserTest(arena)
{
    [Fact]
    public Task ASleepingServer_IsShownWaking_ThenAwake() => WithScreenshotsOnFailureAsync(async () =>
    {
        var page = await NewBrowserAsync();
        // The server answers "not ready" for its first few seconds, as it does while the database resumes.
        var asleepUntil = DateTime.UtcNow.AddSeconds(5);
        await page.RouteAsync("**/health/ready", route => DateTime.UtcNow < asleepUntil
            ? route.FulfillAsync(new() { Status = 503, Body = "{\"status\":\"Unhealthy\"}" })
            : route.ContinueAsync());

        await page.GotoAsync(Arena.ClientUrl);

        var strip = page.Locator(".server-wake");
        await Assertions.Expect(strip).ToContainTextAsync("Waking the game server");
        await Assertions.Expect(strip).ToHaveTextAsync("✅ The game server is awake.", new() { Timeout = 15_000 });
        await Assertions.Expect(page.Locator(".server-wake")).ToHaveCountAsync(0, new() { Timeout = 10_000 });
    });

    [Fact]
    public Task AnAwakeServer_ShowsNothing() => WithScreenshotsOnFailureAsync(async () =>
    {
        var page = await NewBrowserAsync();
        // Answered at once, as an awake server in Azure does. (The test API's own check is slow: it waits for
        // blob storage these tests don't run.)
        var checks = 0;
        await page.RouteAsync("**/health/ready", route =>
        {
            checks++;
            return route.FulfillAsync(new() { Status = 200, Body = "{\"status\":\"Healthy\"}" });
        });

        await page.GotoAsync(Arena.ClientUrl);
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Play as a guest" })).ToBeVisibleAsync();
        await page.WaitForTimeoutAsync(3000);

        await Assertions.Expect(page.Locator(".server-wake")).ToHaveCountAsync(0);
        Assert.Equal(1, checks); // asked once, and awake
    });
}
