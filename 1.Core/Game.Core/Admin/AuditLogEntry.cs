namespace Game.Core.Admin;

public enum AdminAction
{
    /// <summary>A hero was stopped from signing in, for a while or until reinstated.</summary>
    Suspend,

    /// <summary>A suspension was lifted early.</summary>
    Reinstate,

    /// <summary>Gold was added or taken away, such as to make good a bug or undo an exploit.</summary>
    AdjustGold,

    /// <summary>An account became an admin because it is listed in Admin:Usernames.</summary>
    GrantAdmin,

    /// <summary>An account stopped being an admin because it was taken out of Admin:Usernames.</summary>
    RevokeAdmin
}

/// <summary>
/// One thing an admin did to a player's account, kept so every decision can be checked later:
/// who did it, to whom, when, and why. Entries are only ever added, never changed, and the
/// cleanup worker deletes them after a year.
/// </summary>
public class AuditLogEntry
{
    public const int NameMaxLength = 50;
    public const int ReasonMaxLength = 500;
    public const int DetailMaxLength = 200;

    /// <summary>The actor named when the change came from configuration rather than a person.</summary>
    public const string ConfigurationActor = "Configuration";

    public long Id { get; private set; }
    /// <summary>When it happened, in UTC.</summary>
    public DateTime At { get; private set; }
    public AdminAction Action { get; private set; }

    /// <summary>The admin who did it; empty for changes made by configuration.</summary>
    public Guid ActorId { get; private set; }
    public string ActorName { get; private set; } = string.Empty;

    public Guid TargetId { get; private set; }

    /// <summary>The player's name at the time, so the entry still reads sensibly after a rename or deletion.</summary>
    public string TargetName { get; private set; } = string.Empty;

    /// <summary>Why the admin did it, in their words. Shown to a suspended player.</summary>
    public string Reason { get; private set; } = string.Empty;

    /// <summary>What changed, e.g. "+500 gold (1200 → 1700)" or "for 7 days, until 14 Oct 2026".</summary>
    public string? Detail { get; private set; }

    private AuditLogEntry() { }

    public AuditLogEntry(
        DateTime at, AdminAction action, Guid actorId, string actorName, Guid targetId, string targetName,
        string reason, string? detail = null)
    {
        if (!Enum.IsDefined(action)) throw new ArgumentOutOfRangeException(nameof(action));
        if (string.IsNullOrWhiteSpace(actorName)) throw new ArgumentException("Say who did it.", nameof(actorName));
        if (targetId == Guid.Empty) throw new ArgumentException("Say whose account it was.", nameof(targetId));
        if (string.IsNullOrWhiteSpace(targetName)) throw new ArgumentException("Say whose account it was.", nameof(targetName));

        if (at.Kind != DateTimeKind.Utc) throw new ArgumentException("Times are kept in UTC.", nameof(at));
        At = at;
        Action = action;
        ActorId = actorId;
        ActorName = Truncate(actorName.Trim(), NameMaxLength);
        TargetId = targetId;
        TargetName = Truncate(targetName.Trim(), NameMaxLength);
        Reason = RequireReason(reason);
        Detail = detail is null ? null : Truncate(detail, DetailMaxLength);
    }

    /// <summary>A reason an admin gave, trimmed; throws when it is missing or too long.</summary>
    public static string RequireReason(string? reason)
    {
        var trimmed = reason?.Trim() ?? string.Empty;
        if (trimmed.Length == 0) throw new ArgumentException("Give a reason, so the decision can be checked later.", nameof(reason));
        if (trimmed.Length > ReasonMaxLength) throw new ArgumentException($"Keep the reason to {ReasonMaxLength} characters.", nameof(reason));
        return trimmed;
    }

    private static string Truncate(string value, int length) => value.Length <= length ? value : value[..length];
}
