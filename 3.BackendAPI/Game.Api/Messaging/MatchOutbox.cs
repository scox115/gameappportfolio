using System.Text.Json;
using System.Threading.Channels;
using Game.Core.Events;
using Game.Infrastructure.Data;
using Game.Infrastructure.Messaging;

namespace Game.Api.Messaging;

/// <summary>
/// Queues match events in the database, next to the match itself. <see cref="Add"/> only stages the
/// row: the caller's own SaveChanges commits it together with the rewards, so an event is never lost
/// after a match is saved and never sent for one that wasn't. <see cref="MatchOutboxRelay"/> then
/// sends it to RabbitMQ. See docs/adr/0023-transactional-outbox.md.
/// </summary>
public class MatchOutbox(TimeProvider timeProvider)
{
    public const string MatchCompletedQueue = "match-completed-queue";

    // A doorbell for the relay: at most one ring is kept, since one look at the table finds every row.
    private readonly Channel<bool> _doorbell = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });

    /// <summary>Stages the event in <paramref name="dbContext"/>; it is saved by the caller's next SaveChanges.</summary>
    public void Add(AppDbContext dbContext, MatchCompletedEvent matchEvent)
    {
        dbContext.OutboxMessages.Add(new OutboxMessage(
            matchEvent.MatchId,
            MatchCompletedQueue,
            JsonSerializer.Serialize(matchEvent, MatchEventJson.Options),
            timeProvider.GetUtcNow().UtcDateTime));
    }

    /// <summary>Tells the relay a new event was saved, so it goes out now rather than at the next poll.</summary>
    public void Notify() => _doorbell.Writer.TryWrite(true);

    /// <summary>Waits for <see cref="Notify"/>, or for <paramref name="timeout"/> to pass.</summary>
    public async Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timer = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timer.CancelAfter(timeout);
        try
        {
            await _doorbell.Reader.ReadAsync(timer.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Timed out: poll anyway, in case a row was left behind by an earlier run.
        }
    }
}
