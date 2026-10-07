using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Game.E2E.Tests;

// Serves the published Blazor client the way Azure Static Web Apps does: static files, with
// index.html for any other path, and every response carrying the globalHeaders from
// staticwebapp.config.json, Content Security Policy included. Its appsettings.json points the
// game at the test API, and index.html turns on browser telemetry, sent to the test's TelemetrySink.
public sealed class ClientHost : IAsyncDisposable
{
    private readonly WebApplication _app;

    private ClientHost(WebApplication app, string baseUrl)
    {
        _app = app;
        BaseUrl = baseUrl;
    }

    public string BaseUrl { get; }

    /// <summary>Where the test API says uploaded portraits live (see ApiHost's storage stand-in).</summary>
    public const string AvatarOrigin = "https://storage.test";

    /// <summary>The headers every response carries, as Static Web Apps would send them.</summary>
    public IReadOnlyDictionary<string, string> Headers { get; private init; } = new Dictionary<string, string>();

    public static async Task<ClientHost> StartAsync(string wwwroot, int port, string apiBaseUrl, TelemetrySink telemetry)
    {
        var baseUrl = $"http://127.0.0.1:{port}";
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls(baseUrl);
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        var app = builder.Build();

        var settings = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(wwwroot, "appsettings.json")))!;
        settings["ApiBaseUrl"] = apiBaseUrl;
        var settingsJson = settings.ToJsonString();

        var headers = await GlobalHeadersAsync(wwwroot, apiBaseUrl, telemetry.BaseUrl);

        // The meta tags infra/configure-client.py fills in at deploy time.
        var index = (await File.ReadAllTextAsync(Path.Combine(wwwroot, "index.html")))
            .Replace("<meta name=\"telemetry-connection-string\" content=\"\" />",
                $"<meta name=\"telemetry-connection-string\" content=\"{telemetry.ConnectionString}\" />")
            .Replace("<meta name=\"telemetry-api-origin\" content=\"\" />",
                $"<meta name=\"telemetry-api-origin\" content=\"{new Uri(apiBaseUrl).GetLeftPart(UriPartial.Authority)}\" />")
            .Replace("<meta name=\"api-origin\" content=\"\" />",
                $"<meta name=\"api-origin\" content=\"{new Uri(apiBaseUrl).GetLeftPart(UriPartial.Authority)}\" />");
        if (!index.Contains(telemetry.ConnectionString))
        {
            throw new InvalidOperationException("index.html has no empty telemetry-connection-string meta tag to fill in.");
        }
        app.Use((context, next) =>
        {
            foreach (var (name, value) in headers)
            {
                context.Response.Headers[name] = value;
            }
            return next(context);
        });

        app.MapGet("/appsettings.json", () => Results.Text(settingsJson, "application/json"));
        app.Use((context, next) => context.Request.Path == "/index.html"
            ? Results.Content(index, "text/html").ExecuteAsync(context)
            : next(context));
        var files = new PhysicalFileProvider(wwwroot);
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = files,
            ServeUnknownFileTypes = true,
            DefaultContentType = "application/octet-stream",
        });
        app.MapFallback(() => Results.Content(index, "text/html"));

        await app.StartAsync();
        return new ClientHost(app, baseUrl) { Headers = headers };
    }

    // Fills in the policy's deploy-time placeholders the same way infra/configure-client.py does.
    private static async Task<Dictionary<string, string>> GlobalHeadersAsync(string wwwroot, string apiBaseUrl, string telemetryOrigin)
    {
        var config = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(wwwroot, "staticwebapp.config.json")))!;
        var headers = config["globalHeaders"]!.AsObject().ToDictionary(h => h.Key, h => h.Value!.GetValue<string>());

        var index = await File.ReadAllTextAsync(Path.Combine(wwwroot, "index.html"));
        var importMap = Regex.Match(index, "<script type=\"importmap\">(.*?)</script>", RegexOptions.Singleline).Groups[1].Value;
        var api = new Uri(apiBaseUrl).GetLeftPart(UriPartial.Authority);

        headers["Content-Security-Policy"] = headers["Content-Security-Policy"]
            .Replace("__API_ORIGIN__", api)
            .Replace("__API_WS_ORIGIN__", "ws" + api["http".Length..])
            .Replace("__AVATAR_ORIGIN__", AvatarOrigin)
            .Replace("__TELEMETRY_ORIGIN__", telemetryOrigin)
            .Replace("__IMPORTMAP_HASH__", $"'sha256-{Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(importMap)))}'");
        return headers;
    }

    /// <summary>
    /// The published client's wwwroot: E2E_CLIENT_DIR when set (CI publishes once, up front),
    /// otherwise a fresh "dotnet publish" of Game.Client into the test output folder.
    /// </summary>
    public static async Task<string> PublishAsync()
    {
        var preset = Environment.GetEnvironmentVariable("E2E_CLIENT_DIR");
        if (!string.IsNullOrEmpty(preset))
        {
            return WwwRoot(Path.GetFullPath(preset));
        }

        var repoRoot = FindRepoRoot();
        var output = Path.Combine(AppContext.BaseDirectory, "client");
        var project = Path.Combine(repoRoot, "4.Frontend", "Game.Client", "Game.Client.csproj");
        var start = new ProcessStartInfo("dotnet", ["publish", project, "--configuration", "Release", "--output", output])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var publish = Process.Start(start) ?? throw new InvalidOperationException("Could not start dotnet publish.");
        var stdout = publish.StandardOutput.ReadToEndAsync();
        var stderr = publish.StandardError.ReadToEndAsync();
        await publish.WaitForExitAsync();
        if (publish.ExitCode != 0)
        {
            throw new InvalidOperationException($"dotnet publish of the client failed:\n{await stdout}\n{await stderr}");
        }

        return WwwRoot(output);
    }

    public static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();

    // A publish folder keeps the site in wwwroot; accept either that folder or wwwroot itself.
    private static string WwwRoot(string folder) =>
        File.Exists(Path.Combine(folder, "index.html")) ? folder : Path.Combine(folder, "wwwroot");

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "GamePortfolioSolution.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("Could not find GamePortfolioSolution.slnx above the test folder.");
    }
}
