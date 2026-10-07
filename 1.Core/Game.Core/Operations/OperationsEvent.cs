namespace Game.Core.Operations;

public enum OperationsEventKind
{
    /// <summary>An API build started serving players.</summary>
    Release,

    /// <summary>A monthly restore drill finished (see docs/disaster-recovery.md).</summary>
    RestoreDrill
}

/// <summary>A production event shown on the public status page: a release or a restore drill.</summary>
public class OperationsEvent
{
    public const int VersionMaxLength = 50;
    public const int RevisionMaxLength = 100;
    public const int DetailMaxLength = 500;

    public long Id { get; private set; }
    public OperationsEventKind Kind { get; private set; }
    public DateTimeOffset OccurredAt { get; private set; }
    public bool Succeeded { get; private set; }

    /// <summary>The build, e.g. "1.0.31+21e6a73...", for releases.</summary>
    public string? Version { get; private set; }

    /// <summary>The Container Apps revision, for releases. Unique per release, so each is recorded once.</summary>
    public string? Revision { get; private set; }

    /// <summary>A short human-readable summary, e.g. "Restored in 18.1 minutes; 19 tables match."</summary>
    public string? Detail { get; private set; }

    private OperationsEvent() { }

    public static OperationsEvent Release(string version, string revision, DateTimeOffset at) => new()
    {
        Kind = OperationsEventKind.Release,
        OccurredAt = at,
        Succeeded = true,
        Version = Truncate(version, VersionMaxLength),
        Revision = Truncate(revision, RevisionMaxLength)
    };

    public static OperationsEvent RestoreDrill(bool succeeded, string detail, DateTimeOffset at) => new()
    {
        Kind = OperationsEventKind.RestoreDrill,
        OccurredAt = at,
        Succeeded = succeeded,
        Detail = Truncate(detail, DetailMaxLength)
    };

    private static string Truncate(string value, int length) => value.Length <= length ? value : value[..length];
}
