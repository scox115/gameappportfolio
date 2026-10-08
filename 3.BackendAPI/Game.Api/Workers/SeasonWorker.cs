using System.Diagnostics;
using Game.Api.Observability;
using Game.Api.Seasons;

namespace Game.Api.Workers;

/// <summary>
/// Closes finished ranked seasons soon after startup and then every few minutes, so a new season
/// starts within minutes of midnight UTC on the first of the month. Every replica runs it; the
/// database lets only one of them close a season.
/// </summary>
public class SeasonWorker(IServiceScopeFactory scopeFactory, ILogger<SeasonWorker> logger) : BackgroundService
{
    public static readonly TimeSpan FirstRunDelay = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // Real time, not the injected clock: tests move that clock and close seasons themselves.
            await Task.Delay(FirstRunDelay, stoppingToken);
            await RunOnceAsync(stoppingToken);

            using var timer = new PeriodicTimer(Interval);
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
        using var activity = GameTelemetry.ActivitySource.StartActivity("Season close");
        try
        {
            using var scope = scopeFactory.CreateScope();
            var closed = await scope.ServiceProvider.GetRequiredService<SeasonService>().CloseFinishedSeasonsAsync(stoppingToken);
            activity?.SetTag("game.seasons.closed", closed.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A paused or unreachable database shouldn't stop the worker; the next run tries again.
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            logger.LogError(ex, "Closing finished seasons failed; it will try again at the next run.");
        }
    }
}
