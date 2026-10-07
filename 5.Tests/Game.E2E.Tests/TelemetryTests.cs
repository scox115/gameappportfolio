using System.Text.Json.Nodes;
using Microsoft.Playwright;

namespace Game.E2E.Tests;

// Browser telemetry, sent to the TelemetrySink that stands in for Application Insights. Every browser
// test runs with it on, so ContentSecurityPolicyTests also proves the policy lets it through.
public class TelemetryTests(ArenaFixture arena) : BrowserTest(arena)
{
    [Fact]
    public Task Playing_ReportsEachScreen_TheLoadTime_AndTheApiCalls() => WithScreenshotsOnFailureAsync(async () =>
    {
        var page = await CreateHeroAsync(NewHeroName("Telem"));
        var leaderboardCall = page.WaitForRequestAsync(request => request.Url.Contains("/api/v1/players/leaderboard"));
        await page.GetByRole(AriaRole.Button, new() { Name = "Leaderboards" }).ClickAsync();
        await Assertions.Expect(page.GetByText("GLOBAL HERO RANKINGS")).ToBeVisibleAsync();

        // The screen view is sent on arrival, but the leaderboard call is recorded only when its answer comes
        // back, which can be after the next flush; wait for both.
        var items = await SentAsync(page, sent => Views(sent).Contains("Leaderboard") && sent.Any(item =>
            item.Kind() == "RemoteDependencyData" && Name(item).Contains("/api/v1/players/leaderboard")));

        Assert.Equal(["Sign in", "Town", "Leaderboard"], Views(items));
        Assert.Contains(items, item => item.Kind() == "PageviewPerformanceData");
        Assert.All(items, item =>
        {
            Assert.Equal("card-arena-client", item.Tag("ai.cloud.role"));
            Assert.Matches(@"^\d+\.\d+\.\d+", item.Tag("ai.application.ver"));
        });

        // After signing in, telemetry carries the player's id (never their name).
        var town = items.First(item => item.Kind() == "PageviewData" && Name(item) == "Town");
        Assert.True(Guid.TryParse(town.Tag("ai.user.authUserId"), out _));

        // API calls are recorded with their timing, and Blazor's own downloads are not.
        var calls = items.Where(item => item.Kind() == "RemoteDependencyData").ToList();
        var apiHost = new Uri(Arena.ApiUrl).Authority;
        Assert.Contains(calls, call => Name(call).Contains("/api/v1/auth/register") && call.Data()["target"]!.GetValue<string>().Contains(apiHost));
        Assert.Contains(calls, call => Name(call).Contains("/api/v1/players/leaderboard"));
        Assert.DoesNotContain(calls, call => Name(call).Contains("/_framework/"));

        // The call carried a W3C trace header with the same trace id, so in Application Insights it
        // leads to the API request it caused.
        var traceparent = await (await leaderboardCall).HeaderValueAsync("traceparent");
        var leaderboard = calls.First(call => Name(call).Contains("/api/v1/players/leaderboard"));
        Assert.Matches($"^00-{leaderboard.Tag("ai.operation.id")}-[0-9a-f]{{16}}-0[01]$", traceparent);
    });

    [Fact]
    public Task ACrashInThePage_IsReportedAsAnException() => WithScreenshotsOnFailureAsync(async () =>
    {
        var page = await NewBrowserAsync();
        // An answer the status page can't render: a list it expects is missing.
        await page.RouteAsync("**/api/v1/status", route => route.FulfillAsync(new()
        {
            Status = 200,
            ContentType = "application/json",
            Headers = new Dictionary<string, string>
            {
                ["Access-Control-Allow-Origin"] = Arena.ClientUrl,
                ["Access-Control-Allow-Credentials"] = "true",
            },
            Body = """{"status":"Healthy","checkedAt":"2026-10-07T00:00:00Z","checks":null,"version":"1.0.0","awakeSince":"2026-10-07T00:00:00Z","releases":[]}""",
        }));

        await page.GotoAsync($"{Arena.ClientUrl}/status");
        await Assertions.Expect(page.Locator("#blazor-error-ui")).ToBeVisibleAsync();

        var items = await SentAsync(page, sent => sent.Any(item => item.Kind() == "ExceptionData"));

        var exception = items.First(item => item.Kind() == "ExceptionData").Data();
        var details = exception["exceptions"]![0]!;
        Assert.Equal("System.ArgumentNullException", details["typeName"]!.GetValue<string>()); // sorting the missing list
        Assert.Contains("Status", details["stack"]?.GetValue<string>() ?? "");
        Assert.Contains("Renderer", exception["properties"]!["category"]!.GetValue<string>());
    });

    // Asks the page to send what it has queued, then waits for the sink to have what the test needs.
    private async Task<IReadOnlyList<JsonObject>> SentAsync(IPage page, Func<IReadOnlyList<JsonObject>, bool> done)
    {
        var session = await page.EvaluateAsync<string>("() => window.gameTelemetry.sessionId()");
        Assert.False(string.IsNullOrEmpty(session), "Browser telemetry didn't start.");

        IReadOnlyList<JsonObject> items = [];
        for (var attempt = 0; attempt < 50; attempt++)
        {
            await page.EvaluateAsync("() => window.gameTelemetry.flush()");
            items = Arena.Telemetry.ForSession(session);
            if (done(items)) return items;
            await Task.Delay(200);
        }

        throw new Xunit.Sdk.XunitException(
            $"The telemetry never arrived. Sent: {string.Join(", ", items.Select(item => $"{item.Kind()} {Name(item)}"))}");
    }

    private static List<string> Views(IEnumerable<JsonObject> items) =>
        items.Where(item => item.Kind() == "PageviewData").Select(Name).ToList();

    private static string Name(JsonObject item) => item.Data()["name"]?.GetValue<string>() ?? "";
}
