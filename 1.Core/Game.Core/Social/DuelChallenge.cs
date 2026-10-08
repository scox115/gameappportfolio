namespace Game.Core.Social;

/// <summary>
/// One hero inviting a friend to a duel. It stays open for <see cref="OpenFor"/>; accepting it starts
/// the duel, and each hero has at most one open challenge (see docs/adr/0037-friends-and-challenges.md).
/// </summary>
public class DuelChallenge
{
    public const int NetworkMaxLength = 64;

    /// <summary>How long the friend has to answer.</summary>
    public static readonly TimeSpan OpenFor = TimeSpan.FromSeconds(60);

    public Guid Id { get; private set; }

    /// <summary>Who sent it. Also the key: sending another challenge replaces this one.</summary>
    public Guid ChallengerId { get; private set; }

    public Guid ChallengedId { get; private set; }

    /// <summary>The challenger's IP address, so a duel between one person's own accounts can be made practice.</summary>
    public string? ChallengerNetwork { get; private set; }

    public DateTime SentAt { get; private set; }

    public DateTime ExpiresAt { get; private set; }

    private DuelChallenge() { }

    public DuelChallenge(Guid challengerId, Guid challengedId, string? network, DateTime now)
    {
        if (challengerId == challengedId) throw new ArgumentException("A hero can't challenge themselves.", nameof(challengedId));

        Id = Guid.NewGuid();
        ChallengerId = challengerId;
        ChallengedId = challengedId;
        ChallengerNetwork = network is { Length: > NetworkMaxLength } ? network[..NetworkMaxLength] : network;
        SentAt = now;
        ExpiresAt = now + OpenFor;
    }

    public bool IsExpired(DateTime now) => now >= ExpiresAt;

    /// <summary>Whole seconds left to answer, never negative.</summary>
    public int SecondsLeft(DateTime now) => (int)Math.Ceiling(Math.Max(0, (ExpiresAt - now).TotalSeconds));
}
