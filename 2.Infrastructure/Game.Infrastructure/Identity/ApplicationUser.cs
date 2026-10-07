using Microsoft.AspNetCore.Identity;

namespace Game.Infrastructure.Identity;

// Sign-in account managed by ASP.NET Core Identity. Its Id is also the Player's Id,
// so the token's subject claim identifies the player profile directly.
public class ApplicationUser : IdentityUser<Guid>
{
    /// <summary>
    /// The session started by the most recent sign-in. Access tokens carry it, and a token from
    /// an older session is refused, so an account is only signed in on one browser at a time.
    /// </summary>
    public Guid? CurrentSessionId { get; set; }

    public const int SuspensionReasonMaxLength = 500;

    /// <summary>
    /// When an admin's suspension ends; <see cref="DateTimeOffset.MaxValue"/> means until an admin
    /// reinstates the account. Kept apart from Identity's lockout, which is for failed sign-ins.
    /// </summary>
    public DateTimeOffset? SuspendedUntil { get; private set; }

    /// <summary>Why the account was suspended, shown to the player when they try to sign in.</summary>
    public string? SuspensionReason { get; private set; }

    public bool IsSuspended(DateTimeOffset now) => SuspendedUntil is { } until && until > now;

    /// <summary>Stops the account signing in, and ends the session it has now.</summary>
    public void Suspend(DateTimeOffset until, string reason)
    {
        SuspendedUntil = until;
        SuspensionReason = reason.Length <= SuspensionReasonMaxLength ? reason : reason[..SuspensionReasonMaxLength];
        CurrentSessionId = null;
    }

    public void Reinstate()
    {
        SuspendedUntil = null;
        SuspensionReason = null;
    }

    /// <summary>
    /// The normalized name the hero had before an admin renamed it. The player can still sign in with
    /// it, so a rename never locks anyone out, and nobody else can register it.
    /// </summary>
    public string? PreviousNormalizedUserName { get; private set; }

    /// <summary>Records the old name before an admin renames the hero.</summary>
    public void RememberPreviousName() => PreviousNormalizedUserName = NormalizedUserName;

    /// <summary>
    /// The 30-second step of the last authenticator code accepted, so the same code (or an older one)
    /// can't be used a second time, even by someone who watched it being typed.
    /// </summary>
    public long? LastTwoFactorStep { get; private set; }

    public void UsedTwoFactorStep(long step) => LastTwoFactorStep = step;
}
