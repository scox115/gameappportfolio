using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Game.Api.Health;

public static class HealthEndpoints
{
    public const string BasePath = "/health";

    /// <summary>Checks that must pass before the API takes traffic.</summary>
    public const string ReadyTag = "ready";

    public static IServiceCollection AddGameHealthChecks(this IServiceCollection services)
    {
        services.AddHealthChecks()
            .AddCheck<DatabaseHealthCheck>("database", tags: [ReadyTag], timeout: TimeSpan.FromSeconds(5))
            // A slow or missing optional service (even one that times out) only degrades readiness.
            .AddCheck<BlobStorageHealthCheck>("blob-storage", failureStatus: HealthStatus.Degraded, tags: [ReadyTag], timeout: TimeSpan.FromSeconds(5))
            .AddCheck<MessageBrokerHealthCheck>("message-broker", failureStatus: HealthStatus.Degraded, tags: [ReadyTag]);
        return services;
    }

    /// <summary>
    /// /health/live: the process is up and serving requests (no dependency checks), for restart probes.
    /// /health/ready: the database is reachable, so the API can do its job. Blob storage and RabbitMQ
    /// only report Degraded, which still returns 200, because the game works without avatars or telemetry.
    /// </summary>
    public static IEndpointRouteBuilder MapGameHealthChecks(this IEndpointRouteBuilder app)
    {
        app.MapHealthChecks($"{BasePath}/live", new HealthCheckOptions
        {
            Predicate = _ => false,
            ResponseWriter = WriteResponse
        }).AllowAnonymous();

        app.MapHealthChecks($"{BasePath}/ready", new HealthCheckOptions
        {
            Predicate = check => check.Tags.Contains(ReadyTag),
            ResponseWriter = WriteResponse
        }).AllowAnonymous();

        return app;
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    // Status, timings and a short description per check. Exception details stay in the logs, not
    // in an anonymous endpoint.
    private static Task WriteResponse(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json";
        var body = new
        {
            status = report.Status.ToString(),
            totalDurationMs = Math.Round(report.TotalDuration.TotalMilliseconds, 1),
            checks = report.Entries.ToDictionary(
                entry => entry.Key,
                entry => new
                {
                    status = entry.Value.Status.ToString(),
                    durationMs = Math.Round(entry.Value.Duration.TotalMilliseconds, 1),
                    description = entry.Value.Description
                })
        };
        return context.Response.WriteAsync(JsonSerializer.Serialize(body, Json));
    }
}
