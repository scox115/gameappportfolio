namespace Game.Core.Social;

/// <summary>
/// Two heroes who are friends, or a friend request one has sent the other. A pair has at most one
/// row, whoever asked first (see docs/adr/0037-friends-and-challenges.md).
/// </summary>
public class Friendship
{
    /// <summary>How many friends and open requests a hero can have, so the list stays a list.</summary>
    public const int MaxFriends = 50;

    public const int PairKeyLength = 65;

    public Guid Id { get; private set; }

    public Guid RequesterId { get; private set; }

    public Guid AddresseeId { get; private set; }

    /// <summary>The two ids in a fixed order, unique, so two heroes asking each other at once can't make two rows.</summary>
    public string PairKey { get; private set; } = string.Empty;

    public DateTime RequestedAt { get; private set; }

    /// <summary>When the addressee said yes; null while the request is open.</summary>
    public DateTime? AcceptedAt { get; private set; }

    public bool IsAccepted => AcceptedAt is not null;

    private Friendship() { }

    public Friendship(Guid requesterId, Guid addresseeId, DateTime now)
    {
        if (requesterId == addresseeId) throw new ArgumentException("A hero can't befriend themselves.", nameof(addresseeId));

        Id = Guid.NewGuid();
        RequesterId = requesterId;
        AddresseeId = addresseeId;
        PairKey = KeyFor(requesterId, addresseeId);
        RequestedAt = now;
    }

    public static string KeyFor(Guid one, Guid two) =>
        one.CompareTo(two) < 0 ? $"{one:N}:{two:N}" : $"{two:N}:{one:N}";

    public bool Involves(Guid playerId) => RequesterId == playerId || AddresseeId == playerId;

    /// <summary>The other hero in the pair.</summary>
    public Guid OtherThan(Guid playerId) => RequesterId == playerId ? AddresseeId : RequesterId;

    /// <summary>Only the hero who was asked can accept.</summary>
    public void Accept(Guid playerId, DateTime now)
    {
        if (playerId != AddresseeId) throw new InvalidOperationException("Only the hero who was asked can accept a friend request.");
        AcceptedAt ??= now;
    }
}
