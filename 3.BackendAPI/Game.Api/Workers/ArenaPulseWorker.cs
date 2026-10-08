using Game.Api.Hubs;
using Game.Core.Battles;

namespace Game.Api.Workers;

/// <summary>
/// Sends the live lobby numbers when they change (at most every couple of seconds), and keeps this
/// replica's online players counted (see <see cref="ArenaPulse"/>).
/// </summary>
public class ArenaPulseWorker(ArenaPulse pulse, TimeProvider timeProvider, ILogger<ArenaPulseWorker> logger) : BackgroundService
{
    public static readonly TimeSpan PublishInterval = TimeSpan.FromSeconds(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var nextHeartbeat = DateTimeOffset.MinValue;
        using var timer = new PeriodicTimer(PublishInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            if (timeProvider.GetUtcNow() >= nextHeartbeat)
            {
                try
                {
                    await pulse.HeartbeatAsync(stoppingToken);
                    nextHeartbeat = timeProvider.GetUtcNow() + PvpLobbyEntry.HeartbeatInterval;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Failed to refresh who is online.");
                }
            }

            await pulse.PublishIfChangedAsync(stoppingToken);
        }
    }
}
