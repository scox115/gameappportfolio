namespace Game.Infrastructure.Messaging;

/// <summary>
/// A message waiting to be sent to RabbitMQ. It is saved in the same SaveChanges as the change it
/// describes, so the two are committed together: a saved match always gets its event, and a match
/// that failed to save never sends one. The relay deletes the row once the broker confirms it.
/// See docs/adr/0023-transactional-outbox.md.
/// </summary>
public class OutboxMessage
{
    public const int QueueMaxLength = 100;
    public const int ErrorMaxLength = 500;

    public Guid Id { get; private set; }

    /// <summary>The RabbitMQ queue the message goes to.</summary>
    public string Queue { get; private set; } = string.Empty;

    /// <summary>The message body, as JSON.</summary>
    public string Payload { get; private set; } = string.Empty;

    public DateTime CreatedAt { get; private set; }

    /// <summary>Failed sends so far, to spot a message that is stuck.</summary>
    public int Attempts { get; private set; }

    public string? LastError { get; private set; }

    private OutboxMessage() { }

    public OutboxMessage(Guid id, string queue, string payload, DateTime createdAt)
    {
        Id = id;
        Queue = queue;
        Payload = payload;
        CreatedAt = createdAt;
    }

    public void Failed(string error)
    {
        Attempts++;
        LastError = error.Length <= ErrorMaxLength ? error : error[..ErrorMaxLength];
    }
}
