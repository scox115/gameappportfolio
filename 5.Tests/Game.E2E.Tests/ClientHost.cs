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
// game at the test API.
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

    public static async Task<ClientHost> StartAsync(string wwwroot, int port, string apiBaseUrl)
    {
        var baseUrl = $"http://127.0.0.1:{port}";
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls(baseUrl);
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        var app = builder.Build();

        var settings = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(wwwroot, "appsettings.json")))!;
        settings["ApiBaseUrl"] = apiBaseUrl;
        var settingsJson = settings.ToJsonString();

        var headers = await GlobalHeadersAsync(wwwroot, apiBaseUrl);
        app.Use((context, next) =>
        {
            foreach (var (name, value) in headers)
            {
                context.Response.Headers[name] = value;
            }
            return next(context);
        });

        app.MapGet("/appsettings.json", () => Results.Text(settingsJson, "application/json"));
        var files = new PhysicalFileProvider(wwwroot);
        app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = files,
            ServeUnknownFileTypes = true,
            DefaultContentType = "application/octet-stream",
        });
        app.MapFallbackToFile("index.html", new StaticFileOptions { FileProvider = files });

        await app.StartAsync();
        return new ClientHost(app, baseUrl) { Headers = headers };
    }

    // Fills in the policy's deploy-time placeholders the same way infra/set-client-csp.py does.
    private static async Task<Dictionary<string, string>> GlobalHeadersAsync(string wwwroot, string apiBaseUrl)
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
