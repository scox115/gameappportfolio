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

    /// <summary>
    /// Until when a relay has claimed the row. With several API replicas, each relay claims a batch before
    /// sending it, so two replicas never send the same row at once. A relay that dies mid-batch simply lets
    /// its claim run out, and another relay picks the rows up.
    /// </summary>
    public DateTime? ClaimedUntil { get; private set; }

    /// <summary>Changes with every claim; a concurrency token, so two relays can't both claim the same row.</summary>
    public Guid ClaimToken { get; private set; }

    private OutboxMessage() { }

    public OutboxMessage(Guid id, string queue, string payload, DateTime createdAt)
    {
        Id = id;
        Queue = queue;
        Payload = payload;
        CreatedAt = createdAt;
    }

    public bool IsClaimable(DateTime now) => ClaimedUntil is null || ClaimedUntil <= now;

    public void Claim(DateTime until)
    {
        ClaimedUntil = until;
        ClaimToken = Guid.NewGuid();
    }

    /// <summary>Gives the row back, so any relay can send it next time.</summary>
    public void Release()
    {
        ClaimedUntil = null;
        ClaimToken = Guid.NewGuid();
    }

    public void Failed(string error)
    {
        Release();
        Attempts++;
        LastError = error.Length <= ErrorMaxLength ? error : error[..ErrorMaxLength];
    }
}
