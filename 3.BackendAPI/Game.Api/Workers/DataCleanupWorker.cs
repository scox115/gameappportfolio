using System.Diagnostics;
using Game.Api.Observability;
using Game.Api.Options;
using Microsoft.Extensions.Options;

namespace Game.Api.Workers;

/// <summary>
/// Runs <see cref="DataCleanupService"/> shortly after startup and then on a fixed interval.
/// Each run is a trace span, and the rows it deletes are counted in game.cleanup.deleted.
/// </summary>
public class DataCleanupWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<CleanupOptions> options,
    TimeProvider timeProvider,
    ILogger<DataCleanupWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        if (!settings.Enabled)
        {
            logger.LogInformation("Scheduled data cleanup is turned off.");
            return;
        }

        try
        {
            await Task.Delay(settings.FirstRunDelay, timeProvider, stoppingToken);
            await RunOnceAsync(stoppingToken);

            using var timer = new PeriodicTimer(settings.Interval, timeProvider);
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await RunOnceAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
    }

    private async Task RunOnceAsync(CancellationToken stoppingToken)
    {
        using var activity = GameTelemetry.ActivitySource.StartActivity("Data cleanup");
        try
        {
            using var scope = scopeFactory.CreateScope();
            var result = await scope.ServiceProvider.GetRequiredService<DataCleanupService>().RunAsync(stoppingToken);

            activity?.SetTag("game.cleanup.deleted", result.Total);
            logger.LogInformation(
                "Data cleanup deleted {RefreshTokens} expired refresh tokens, {EmailLinks} expired email links, {BossFights} old boss fights, {Duels} old duels and {AuditEntries} old audit log entries.",
                result.ExpiredRefreshTokens, result.ExpiredEmailLinks, result.FinishedBossFights, result.FinishedDuels, result.OldAuditEntries);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The next run tries again; a paused or unreachable database shouldn't stop the worker.
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            logger.LogError(ex, "Data cleanup failed; it will try again at the next run.");
        }
    }
}
