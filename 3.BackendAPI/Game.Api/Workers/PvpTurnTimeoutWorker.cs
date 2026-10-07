using Game.Api.Hubs;
using Game.Core.Battles;

namespace Game.Api.Workers;

/// <summary>
/// Ends PvP battles whose active player ran out of time (or left), so their opponent is never
/// left waiting. Moves made after the deadline are rejected as well, so this only speeds things up.
/// It plays the Arena Bot's turns too (see <see cref="PvpBattleService.PlayBotTurnsAsync"/>), and
/// keeps this replica's lobby entries fresh (see <see cref="PvpMatchmaker.HeartbeatAsync"/>).
/// </summary>
/// <remarks>
/// Every replica runs this. Two replicas settling the same battle is harmless: the battle row has a
/// concurrency token, so the second save fails and the first result stands.
/// </remarks>
public class PvpTurnTimeoutWorker(
    IServiceScopeFactory scopeFactory,
    PvpMatchmaker matchmaker,
    TimeProvider timeProvider,
    ILogger<PvpTurnTimeoutWorker> logger) : BackgroundService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var nextHeartbeat = DateTimeOffset.MinValue;
        using var timer = new PeriodicTimer(CheckInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var battles = scope.ServiceProvider.GetRequiredService<PvpBattleService>();
                await battles.ExpireOverdueTurnsAsync(stoppingToken);
                await battles.PlayBotTurnsAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Failed to settle timed-out PvP battles or play the Arena Bot's turns.");
            }

            if (timeProvider.GetUtcNow() < nextHeartbeat) continue;
            try
            {
                await matchmaker.HeartbeatAsync(stoppingToken);
                nextHeartbeat = timeProvider.GetUtcNow() + PvpLobbyEntry.HeartbeatInterval;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Failed to refresh the PvP lobby.");
            }
        }
    }
}
