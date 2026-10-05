namespace Game.Client.Services;

/// <summary>How long a signed-in player can be inactive before they are signed out.</summary>
public record IdleTimeoutSettings(TimeSpan Timeout)
{
    /// <summary>The warning appears this long before the sign-out.</summary>
    public TimeSpan WarningPeriod { get; } = TimeSpan.FromSeconds(Math.Min(60, Timeout.TotalSeconds / 2));
}
