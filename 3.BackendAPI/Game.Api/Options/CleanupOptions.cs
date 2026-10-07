using System.ComponentModel.DataAnnotations;

namespace Game.Api.Options;

// Bound from the "Cleanup" configuration section; see DataCleanupWorker.
public class CleanupOptions
{
    public const string SectionName = "Cleanup";

    /// <summary>Turns the scheduled cleanup off (the tests do, since the in-memory database can't bulk delete).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// How long after startup the first run happens. The API scales to zero in Azure, so a fixed
    /// time of day might never come round; running soon after each start means it always gets a turn.
    /// </summary>
    [Range(typeof(TimeSpan), "00:00:00", "1.00:00:00")]
    public TimeSpan FirstRunDelay { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Time between runs while the API keeps running.</summary>
    [Range(typeof(TimeSpan), "00:01:00", "7.00:00:00")]
    public TimeSpan Interval { get; set; } = TimeSpan.FromHours(6);

    /// <summary>Days a refresh token is kept after it expires, to help explain a sign-out.</summary>
    [Range(0, 365)]
    public int ExpiredTokenRetentionDays { get; set; } = 7;

    /// <summary>
    /// Days a finished battle's turn-by-turn state is kept. Results live on in match history, the
    /// match record and the player's stats; this is only the working state of the fight.
    /// </summary>
    [Range(2, 3650)]
    public int FinishedBattleRetentionDays { get; set; } = 30;

    /// <summary>
    /// Days an admin audit log entry is kept: long enough to look back on a decision or an appeal,
    /// not forever, since entries name players. The privacy policy promises one year.
    /// </summary>
    [Range(30, 3650)]
    public int AuditLogRetentionDays { get; set; } = 365;

    /// <summary>Rows deleted per statement, so a big backlog never holds long locks.</summary>
    [Range(1, 100_000)]
    public int BatchSize { get; set; } = 1_000;
}
