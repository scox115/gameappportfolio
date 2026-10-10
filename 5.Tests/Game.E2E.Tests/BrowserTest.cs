using Microsoft.Playwright;

namespace Game.E2E.Tests;

// Helpers the browser tests share: fresh browsers, signing up, and screenshots when a test fails.
[Collection(ArenaCollection.Name)]
[Trait("Category", "Browser")]
public abstract class BrowserTest(ArenaFixture arena) : IAsyncLifetime
{
    public const string Password = "Arena-Pass1";

    private readonly List<IBrowserContext> _contexts = [];

    protected ArenaFixture Arena { get; } = arena;

    static BrowserTest()
    {
        // Blazor WebAssembly takes a moment to boot, and CI machines are slow.
        Assertions.SetDefaultExpectTimeout(30_000);
    }

    /// <summary>A browser with its own storage, like a second computer.</summary>
    protected async Task<IPage> NewBrowserAsync()
    {
        var context = await Arena.Browser.NewContextAsync(new() { ViewportSize = new() { Width = 1280, Height = 900 } });
        context.SetDefaultTimeout(30_000);
        // Anything the Content Security Policy blocks is recorded for ContentSecurityPolicyTests.
        await context.AddInitScriptAsync("""
            window.__cspViolations = [];
            document.addEventListener('securitypolicyviolation',
                e => window.__cspViolations.push(`${e.effectiveDirective} blocked ${e.blockedURI}`));
            """);
        _contexts.Add(context);
        return await context.NewPageAsync();
    }

    protected static string NewHeroName(string prefix) => $"{prefix}{Guid.NewGuid():N}"[..12];

    protected async Task<IPage> CreateHeroAsync(string name, string heroClass = "Ranger")
    {
        var page = await NewBrowserAsync();
        await page.GotoAsync(Arena.ClientUrl);
        await page.GetByRole(AriaRole.Button, new() { Name = "Create a new hero" }).ClickAsync();
        await page.GetByPlaceholder("Enter Character name...").FillAsync(name);
        await page.GetByPlaceholder("At least 8 characters...").FillAsync(Password);
        await page.Locator($"button[aria-pressed]:has-text(\"{heroClass}\")").ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Create Hero" }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "LAUNCH BATTLE ARENA" })).ToBeVisibleAsync();
        return page;
    }

    /// <summary>An admin hero with two-factor sign-in on, which the admin tools need.</summary>
    protected async Task<IPage> CreateAdminAsync(string name)
    {
        var page = await CreateHeroAsync(name);
        await Arena.TurnOnTwoFactorAsync(name);
        return page;
    }

    protected async Task<IPage> SignInAsync(string name)
    {
        var page = await NewBrowserAsync();
        await page.GotoAsync(Arena.ClientUrl);
        await page.GetByPlaceholder("Enter Character name...").FillAsync(name);
        await page.GetByPlaceholder("At least 8 characters...").FillAsync(Password);
        await page.GetByRole(AriaRole.Button, new() { Name = "Play Game" }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "LAUNCH BATTLE ARENA" })).ToBeVisibleAsync();
        return page;
    }

    /// <summary>Runs the test body and, if it fails, saves a screenshot of every open page.</summary>
    protected async Task WithScreenshotsOnFailureAsync(Func<Task> body, [System.Runtime.CompilerServices.CallerMemberName] string test = "")
    {
        try
        {
            await body();
        }
        catch
        {
            var folder = Environment.GetEnvironmentVariable("E2E_SCREENSHOTS_DIR")
                ?? Path.Combine(AppContext.BaseDirectory, "screenshots");
            Directory.CreateDirectory(folder);
            var index = 0;
            foreach (var page in _contexts.SelectMany(c => c.Pages))
            {
                await page.ScreenshotAsync(new() { Path = Path.Combine(folder, $"{test}-{++index}.png"), FullPage = true });
            }

            throw;
        }
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var context in _contexts)
        {
            await context.DisposeAsync();
        }
    }
}
