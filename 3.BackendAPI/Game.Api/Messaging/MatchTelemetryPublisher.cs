using System.Threading.Channels;
using Game.Core.Events;

namespace Game.Api.Messaging;

// Queues a telemetry notice after a match is settled. Rewards are already saved by then, so this
// only drops the event into memory and returns; MatchTelemetrySender delivers it to RabbitMQ in the
// background. A slow or missing broker therefore never delays or fails the player's request.
public class MatchTelemetryPublisher(ILogger<MatchTelemetryPublisher> logger)
{
    public const string MatchCompletedQueue = "match-completed-queue";

    /// <summary>Events waiting to be sent. If the broker is down for long, the oldest are dropped first.</summary>
    public const int Capacity = 1_000;

    private readonly Channel<MatchCompletedEvent> _pending = Channel.CreateBounded<MatchCompletedEvent>(
        new BoundedChannelOptions(Capacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true
        },
        dropped => logger.LogWarning("Telemetry backlog is full; dropped match {MatchId}.", dropped.MatchId));

    public ChannelReader<MatchCompletedEvent> Pending => _pending.Reader;

    public Task PublishAsync(MatchCompletedEvent matchEvent, CancellationToken cancellationToken = default)
    {
        _pending.Writer.TryWrite(matchEvent);
        return Task.CompletedTask;
    }
}
