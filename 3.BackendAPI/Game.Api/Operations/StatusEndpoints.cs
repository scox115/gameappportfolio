using Game.Api.Caching;
using Game.Api.Health;
using Game.Core.Operations;
using Game.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Game.Api.Operations;

public record StatusCheck(string Name, string Status, string? Description);

public record StatusRelease(string Version, string? Commit, string? Revision, DateTimeOffset At);

public record StatusDrill(DateTimeOffset At, bool Succeeded, string? Detail);

public record StatusResponse(
    string Status,
    DateTimeOffset CheckedAt,
    IReadOnlyList<StatusCheck> Checks,
    string Version,
    string? Commit,
    string? Revision,
    DateTimeOffset AwakeSince,
    IReadOnlyList<StatusRelease> Releases,
    StatusDrill? LastRestoreDrill);

public static class StatusEndpoints
{
    public const int ReleasesShown = 5;

    public static void MapStatusEndpoints(this IEndpointRouteBuilder app)
    {
        // The public status page's data: the same checks as /health/ready, what is running, the last few
        // releases and the last restore drill. Cached briefly, because a status page gets refreshed a lot
        // during an incident and every check wakes the database.
        app.MapGet("/status", async (HealthCheckService health, BuildInfo build, AppDbContext db, TimeProvider clock,
                ILogger<StatusResponse> logger, CancellationToken cancellationToken) =>
            {
                var report = await health.CheckHealthAsync(check => check.Tags.Contains(HealthEndpoints.ReadyTag), cancellationToken);
                var checks = report.Entries
                    .Select(entry => new StatusCheck(entry.Key, entry.Value.Status.ToString(), entry.Value.Description))
                    .ToList();

                List<StatusRelease> releases = [];
                StatusDrill? drill = null;
                if (report.Entries.TryGetValue("database", out var database) && database.Status == HealthStatus.Healthy)
                {
                    try
                    {
                        var recent = await db.OperationsEvents.AsNoTracking()
                            .Where(e => e.Kind == OperationsEventKind.Release)
                            .OrderByDescending(e => e.OccurredAt)
                            .Take(ReleasesShown)
                            .ToListAsync(cancellationToken);
                        releases = recent.Select(e => ToRelease(e)).ToList();

                        drill = await db.OperationsEvents.AsNoTracking()
                            .Where(e => e.Kind == OperationsEventKind.RestoreDrill)
                            .OrderByDescending(e => e.OccurredAt)
                            .Select(e => new StatusDrill(e.OccurredAt, e.Succeeded, e.Detail))
                            .FirstOrDefaultAsync(cancellationToken);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // The checks above are the important part; history is a nice-to-have.
                        logger.LogWarning(ex, "Couldn't read the operations history for the status page.");
                    }
                }

                return Results.Ok(new StatusResponse(
                    report.Status.ToString(), clock.GetUtcNow(), checks,
                    build.Version, build.Commit, build.Revision, build.StartedAt,
                    releases, drill));
            })
            .WithTags("Status")
            .AllowAnonymous()
            .CacheOutput(OutputCaching.Policies.Status);
    }

    private static StatusRelease ToRelease(OperationsEvent release)
    {
        var parts = (release.Version ?? "").Split('+');
        var commit = parts is [_, var sha, ..] ? sha[..Math.Min(12, sha.Length)] : null;
        return new StatusRelease(parts[0], commit, release.Revision, release.OccurredAt);
    }
}
