using Game.Api.Hubs;

namespace Game.Api.Workers;

/// <summary>
/// Ends PvP battles whose active player ran out of time (or left), so their opponent is never
/// left waiting. Moves made after the deadline are rejected as well, so this only speeds things up.
/// </summary>
public class PvpTurnTimeoutWorker(IServiceScopeFactory scopeFactory, ILogger<PvpTurnTimeoutWorker> logger) : BackgroundService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(CheckInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var battles = scope.ServiceProvider.GetRequiredService<PvpBattleService>();
                await battles.ExpireOverdueTurnsAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Failed to settle timed-out PvP battles.");
            }
        }
    }
}
