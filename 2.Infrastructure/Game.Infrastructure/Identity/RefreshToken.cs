namespace Game.Infrastructure.Identity;

/// <summary>
/// A long-lived token a client trades for a new access token. Only a hash is stored, each
/// token can be used once (it is replaced on every refresh), and it can be revoked.
/// </summary>
public class RefreshToken
{
    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }

    /// <summary>SHA-256 of the token, so a database leak doesn't expose usable tokens.</summary>
    public string TokenHash { get; private set; } = string.Empty;

    public DateTime CreatedAt { get; private set; }
    public DateTime ExpiresAt { get; private set; }
    public DateTime? RevokedAt { get; private set; }

    /// <summary>The token issued when this one was used, if it was.</summary>
    public Guid? ReplacedByTokenId { get; private set; }

    private RefreshToken() { }

    public RefreshToken(Guid userId, string tokenHash, DateTime createdAt, DateTime expiresAt)
    {
        if (userId == Guid.Empty) throw new ArgumentException("User id cannot be empty.", nameof(userId));
        if (string.IsNullOrWhiteSpace(tokenHash)) throw new ArgumentException("Token hash is required.", nameof(tokenHash));
        if (expiresAt <= createdAt) throw new ArgumentException("A token must expire after it is created.", nameof(expiresAt));

        Id = Guid.NewGuid();
        UserId = userId;
        TokenHash = tokenHash;
        CreatedAt = createdAt;
        ExpiresAt = expiresAt;
    }

    public bool IsRevoked => RevokedAt is not null;

    public bool IsActive(DateTime now) => !IsRevoked && now < ExpiresAt;

    public void Revoke(DateTime now, Guid? replacedByTokenId = null)
    {
        if (IsRevoked) return;
        RevokedAt = now;
        ReplacedByTokenId = replacedByTokenId;
    }
}
