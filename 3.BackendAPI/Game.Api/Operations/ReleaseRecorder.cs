using Game.Core.Operations;
using Game.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Game.Api.Operations;

/// <summary>
/// Records a release on the status page the first time this revision serves a request on the app's public
/// address. With blue-green deploys that happens when traffic switches to it, not when it starts, so a
/// revision that fails its smoke test never shows up as released.
/// </summary>
public sealed class ReleaseRecorder(BuildInfo build, IServiceScopeFactory scopes, TimeProvider clock, ILogger<ReleaseRecorder> logger)
{
    private static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(1);

    private int _recording; // 1 while one request is recording, so others don't wait on it
    private bool _done;
    private DateTimeOffset _nextAttempt = DateTimeOffset.MinValue;

    public bool IsDone => _done;

    public async Task OnRequestAsync(HttpContext context)
    {
        if (_done || build.Revision is null || build.PublicHost is null) return;
        if (!string.Equals(context.Request.Host.Host, build.PublicHost, StringComparison.OrdinalIgnoreCase)) return;
        if (clock.GetUtcNow() < _nextAttempt || Interlocked.Exchange(ref _recording, 1) == 1) return;

        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            if (!await db.OperationsEvents.AnyAsync(e => e.Revision == build.Revision))
            {
                db.OperationsEvents.Add(OperationsEvent.Release(build.FullVersion, build.Revision, clock.GetUtcNow()));
                await db.SaveChangesAsync();
                logger.LogInformation("Recorded release {Version} on revision {Revision}.", build.Version, build.Revision);
            }
            _done = true;
        }
        catch (DbUpdateException)
        {
            _done = true; // another replica recorded it first (the revision is unique)
        }
        catch (Exception ex)
        {
            // Never fail a player's request over the status page; try again a little later.
            logger.LogWarning(ex, "Couldn't record release {Version}; will retry.", build.Version);
            _nextAttempt = clock.GetUtcNow() + RetryAfter;
        }
        finally
        {
            Interlocked.Exchange(ref _recording, 0);
        }
    }
}

public static class ReleaseRecording
{
    public static IApplicationBuilder UseReleaseRecording(this IApplicationBuilder app)
    {
        var recorder = app.ApplicationServices.GetRequiredService<ReleaseRecorder>();
        return app.Use(async (context, next) =>
        {
            await recorder.OnRequestAsync(context);
            await next(context);
        });
    }
}
