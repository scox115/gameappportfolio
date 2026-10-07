using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Game.E2E.Tests;

// Stands in for Application Insights' ingestion endpoint: the client's connection string points here,
// on an origin of its own as in Azure, and every telemetry item the browser sends is kept for the
// tests to look at.
public sealed class TelemetrySink : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly ConcurrentQueue<JsonObject> _items = new();

    private TelemetrySink(WebApplication app, string baseUrl)
    {
        _app = app;
        BaseUrl = baseUrl;
    }

    public string BaseUrl { get; }

    public string ConnectionString =>
        $"InstrumentationKey=00000000-0000-0000-0000-00000000e2e0;IngestionEndpoint={BaseUrl}/";

    public static async Task<TelemetrySink> StartAsync(int port)
    {
        var baseUrl = $"http://127.0.0.1:{port}";
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls(baseUrl);
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        var app = builder.Build();
        var sink = new TelemetrySink(app, baseUrl);

        // The browser sends cross-origin, as it would to Azure, so answer CORS like the real endpoint.
        app.Use((context, next) =>
        {
            context.Response.Headers.AccessControlAllowOrigin = context.Request.Headers.Origin.FirstOrDefault() ?? "*";
            context.Response.Headers.AccessControlAllowCredentials = "true";
            context.Response.Headers.AccessControlAllowHeaders = "*";
            context.Response.Headers.AccessControlAllowMethods = "POST, OPTIONS";
            return HttpMethods.IsOptions(context.Request.Method) ? Task.CompletedTask : next(context);
        });

        app.MapPost("/v2/track", async (HttpRequest request) =>
        {
            using var reader = new StreamReader(request.Body);
            var body = await reader.ReadToEndAsync();
            // A JSON array, or one item per line when the SDK sends with sendBeacon.
            var items = body.TrimStart().StartsWith('[')
                ? JsonNode.Parse(body)!.AsArray().Select(node => node!.AsObject()).ToList()
                : body.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonNode.Parse(line)!.AsObject()).ToList();
            foreach (var item in items) sink._items.Enqueue(item);
            return Results.Json(new { itemsReceived = items.Count, itemsAccepted = items.Count, errors = Array.Empty<object>() });
        });

        await app.StartAsync();
        return sink;
    }

    /// <summary>Everything one page load sent, by the SDK's session id.</summary>
    public IReadOnlyList<JsonObject> ForSession(string sessionId) =>
        _items.Where(item => item["tags"]?["ai.session.id"]?.GetValue<string>() == sessionId).ToList();

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();
}

public static class TelemetryItem
{
    /// <summary>"PageviewData", "PageviewPerformanceData", "RemoteDependencyData", "ExceptionData"...</summary>
    public static string Kind(this JsonObject item) => item["data"]?["baseType"]?.GetValue<string>() ?? "";

    public static JsonNode Data(this JsonObject item) => item["data"]!["baseData"]!;

    public static string? Tag(this JsonObject item, string name) => item["tags"]?[name]?.GetValue<string>();
}
