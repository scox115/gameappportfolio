using System.ComponentModel.DataAnnotations;

namespace Game.Api.Options;

// Bound from the "AntiCheat" configuration section.
public class AntiCheatOptions
{
    public const string SectionName = "AntiCheat";

    /// <summary>
    /// Duels between two players on the same IP address pay nothing and can't be wagered on, so a
    /// player can't farm a second account. Off in local development, where one person plays both sides.
    /// </summary>
    public bool SameNetworkDuelsArePractice { get; set; } = true;

    /// <summary>New accounts allowed per IP address per hour.</summary>
    [Range(1, int.MaxValue)]
    public int RegistrationsPerHour { get; set; } = 3;

    /// <summary>
    /// Guest heroes allowed per IP address per hour. More than sign-ups, since a whole office may try the
    /// game from one address; a guest can't wager, so a pile of them is worth little.
    /// </summary>
    [Range(1, int.MaxValue)]
    public int GuestsPerHour { get; set; } = 20;

    /// <summary>Sign-in attempts allowed per IP address per minute.</summary>
    [Range(1, int.MaxValue)]
    public int SignInsPerMinute { get; set; } = 10;

    /// <summary>Portrait uploads allowed per player per hour, so nobody can fill up blob storage.</summary>
    [Range(1, int.MaxValue)]
    public int AvatarUploadsPerHour { get; set; } = 10;

    /// <summary>Account recovery requests (reset links, confirmation emails) allowed per IP address per hour.</summary>
    [Range(1, int.MaxValue)]
    public int RecoveryRequestsPerHour { get; set; } = 10;

    /// <summary>Reports a player can file per hour, so the admin queue can't be flooded.</summary>
    [Range(1, int.MaxValue)]
    public int ReportsPerHour { get; set; } = 10;
}
