using Microsoft.Playwright;

namespace Game.E2E.Tests;

// Starts the API, the published client, a stand-in for Application Insights and one Chromium for
// every browser test, once.
public sealed class ArenaFixture : IAsyncLifetime
{
    private ApiHost? _api;
    private ClientHost? _client;
    private TelemetrySink? _telemetry;
    private IPlaywright? _playwright;

    public IBrowser Browser { get; private set; } = null!;

    public string ClientUrl => _client!.BaseUrl;

    public IReadOnlyDictionary<string, string> ClientHeaders => _client!.Headers;

    public string ApiUrl => _api!.BaseUrl;

    public ApiHost.FeatureSwitches Features => _api!.Features;

    /// <summary>What the browsers sent to "Application Insights".</summary>
    public TelemetrySink Telemetry => _telemetry!;

    public async Task InitializeAsync()
    {
        var wwwroot = await ClientHost.PublishAsync();

        var apiPort = ClientHost.FreePort();
        var clientPort = ClientHost.FreePort();
        _api = new ApiHost(apiPort, $"http://127.0.0.1:{clientPort}");
        _api.StartServer();
        _telemetry = await TelemetrySink.StartAsync(ClientHost.FreePort());
        _client = await ClientHost.StartAsync(wwwroot, clientPort, _api.BaseUrl, _telemetry);

        _playwright = await Playwright.CreateAsync();
        Browser = await LaunchChromiumAsync(_playwright);
    }

    public async Task DisposeAsync()
    {
        if (Browser is not null) await Browser.DisposeAsync();
        _playwright?.Dispose();
        if (_client is not null) await _client.DisposeAsync();
        if (_telemetry is not null) await _telemetry.DisposeAsync();
        if (_api is not null) await _api.DisposeAsync();
    }

    // The first run on a new machine downloads Chromium (CI installs it up front).
    private static async Task<IBrowser> LaunchChromiumAsync(IPlaywright playwright)
    {
        // HEADED=1 opens a visible window, to watch the tests play.
        var options = new BrowserTypeLaunchOptions { Headless = Environment.GetEnvironmentVariable("HEADED") != "1" };
        try
        {
            return await playwright.Chromium.LaunchAsync(options);
        }
        catch (PlaywrightException ex) when (ex.Message.Contains("Executable doesn't exist"))
        {
            var exitCode = Microsoft.Playwright.Program.Main(["install", "chromium"]);
            if (exitCode != 0)
            {
                throw new InvalidOperationException($"Installing Chromium for Playwright failed (exit code {exitCode}).", ex);
            }

            return await playwright.Chromium.LaunchAsync(options);
        }
    }
}

[CollectionDefinition(Name)]
public class ArenaCollection : ICollectionFixture<ArenaFixture>
{
    public const string Name = "Arena";
}
