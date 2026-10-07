namespace Game.Core.Moderation;

public enum ReportReason
{
    /// <summary>The hero's name is offensive or pretends to be staff.</summary>
    Name,

    /// <summary>The hero's portrait is offensive.</summary>
    Portrait
}

public enum ReportOutcome
{
    /// <summary>An admin looked and left the name or portrait as it is.</summary>
    Dismissed,

    /// <summary>An admin renamed the hero or removed the portrait.</summary>
    ActionTaken
}

/// <summary>
/// One player's report that another hero's name or portrait is offensive. Reports wait in the admin
/// page's queue until an admin acts on them or dismisses them; the cleanup worker deletes them some
/// time after that. The hero who was reported is never told who reported them.
/// </summary>
public class PlayerReport
{
    public const int NoteMaxLength = 200;

    public long Id { get; private set; }
    public Guid ReporterId { get; private set; }
    public Guid TargetId { get; private set; }
    public ReportReason Reason { get; private set; }

    /// <summary>Anything the reporter added, such as what the name means.</summary>
    public string? Note { get; private set; }

    /// <summary>When it was made, in UTC.</summary>
    public DateTime CreatedAt { get; private set; }

    /// <summary>When an admin dealt with it, in UTC; null while it waits.</summary>
    public DateTime? ResolvedAt { get; private set; }
    public ReportOutcome? Outcome { get; private set; }

    public bool IsOpen => ResolvedAt is null;

    private PlayerReport() { }

    public PlayerReport(Guid reporterId, Guid targetId, ReportReason reason, string? note, DateTime createdAt)
    {
        if (reporterId == Guid.Empty || targetId == Guid.Empty) throw new ArgumentException("Say who reported whom.");
        if (reporterId == targetId) throw new ArgumentException("You can't report your own hero.");
        if (!Enum.IsDefined(reason)) throw new ArgumentException("Choose what you're reporting: the name or the portrait.", nameof(reason));
        if (createdAt.Kind != DateTimeKind.Utc) throw new ArgumentException("Times are kept in UTC.", nameof(createdAt));

        var trimmed = note?.Trim();
        if (trimmed?.Length > NoteMaxLength) throw new ArgumentException($"Keep the note to {NoteMaxLength} characters.", nameof(note));

        ReporterId = reporterId;
        TargetId = targetId;
        Reason = reason;
        Note = string.IsNullOrEmpty(trimmed) ? null : trimmed;
        CreatedAt = createdAt;
    }

    public void Resolve(ReportOutcome outcome, DateTime at)
    {
        if (!IsOpen) throw new InvalidOperationException("This report has already been dealt with.");
        if (at.Kind != DateTimeKind.Utc) throw new ArgumentException("Times are kept in UTC.", nameof(at));
        Outcome = outcome;
        ResolvedAt = at;
    }
}
