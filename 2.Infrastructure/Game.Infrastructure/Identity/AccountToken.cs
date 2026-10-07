namespace Game.Infrastructure.Identity;

public enum AccountTokenPurpose
{
    /// <summary>Proves the player can read the email address they added.</summary>
    ConfirmEmail,

    /// <summary>Lets the player choose a new password.</summary>
    ResetPassword
}

/// <summary>
/// A one-time link sent by email. Like refresh tokens, only a hash is stored, each works once, and it
/// expires; unlike Identity's built-in tokens it needs no Data Protection keys, which a container that
/// scales to zero would lose. See docs/adr/0022-account-recovery-by-email.md.
/// </summary>
public class AccountToken
{
    public const int EmailMaxLength = 256;

    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public AccountTokenPurpose Purpose { get; private set; }

    /// <summary>SHA-256 of the token, so a database leak doesn't expose usable links.</summary>
    public string TokenHash { get; private set; } = string.Empty;

    /// <summary>The address being confirmed, for <see cref="AccountTokenPurpose.ConfirmEmail"/>.</summary>
    public string? Email { get; private set; }

    public DateTime CreatedAt { get; private set; }
    public DateTime ExpiresAt { get; private set; }
    public DateTime? UsedAt { get; private set; }

    private AccountToken() { }

    public AccountToken(Guid userId, AccountTokenPurpose purpose, string tokenHash, DateTime createdAt, TimeSpan lifetime, string? email = null)
    {
        if (userId == Guid.Empty) throw new ArgumentException("User id cannot be empty.", nameof(userId));
        if (string.IsNullOrWhiteSpace(tokenHash)) throw new ArgumentException("Token hash is required.", nameof(tokenHash));
        if (lifetime <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(lifetime));
        if (purpose == AccountTokenPurpose.ConfirmEmail && string.IsNullOrWhiteSpace(email))
            throw new ArgumentException("Say which address is being confirmed.", nameof(email));

        Id = Guid.NewGuid();
        UserId = userId;
        Purpose = purpose;
        TokenHash = tokenHash;
        Email = email;
        CreatedAt = createdAt;
        ExpiresAt = createdAt + lifetime;
    }

    public bool IsUsable(DateTime now) => UsedAt is null && now < ExpiresAt;

    /// <summary>Uses the token up (or retires it when a newer one replaces it).</summary>
    public void Use(DateTime now) => UsedAt ??= now;
}
