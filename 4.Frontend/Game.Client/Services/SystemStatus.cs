namespace Game.Client.Services;

// The API's /api/v1/status response, shown on the public status page.
public record SystemStatus(
    string Status,
    DateTimeOffset CheckedAt,
    List<SystemStatusCheck> Checks,
    string Version,
    string? Commit,
    string? Revision,
    DateTimeOffset AwakeSince,
    List<SystemStatusRelease> Releases,
    SystemStatusDrill? LastRestoreDrill);

public record SystemStatusCheck(string Name, string Status, string? Description);

public record SystemStatusRelease(string Version, string? Commit, string? Revision, DateTimeOffset At);

public record SystemStatusDrill(DateTimeOffset At, bool Succeeded, string? Detail);
