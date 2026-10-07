namespace Game.Core.Battles;

/// <summary>
/// A player waiting in the PvP lobby. The lobby is a database table so every API replica sees the
/// same queue: a player connected to one replica can be paired with a player connected to another.
/// See docs/adr/0027-scale-out.md.
/// </summary>
public class PvpLobbyEntry
{
    public const int NetworkMaxLength = 64;

    /// <summary>
    /// How long an entry counts without a heartbeat. The replica holding the player's connection
    /// refreshes it every few seconds, so an entry left behind by a replica that crashed soon stops
    /// pairing anyone.
    /// </summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(45);

    /// <summary>How often the replica holding the connection refreshes <see cref="SeenAt"/>.</summary>
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(15);

    /// <summary>One entry per player: joining again replaces the old one.</summary>
    public Guid PlayerId { get; private set; }

    public int Wager { get; private set; }

    /// <summary>The player's IP address, or null when it isn't known.</summary>
    public string? Network { get; private set; }

    public DateTime JoinedAt { get; private set; }

    /// <summary>When the replica holding the player's connection last vouched that they are still there.</summary>
    public DateTime SeenAt { get; private set; }

    private PvpLobbyEntry() { }

    public PvpLobbyEntry(Guid playerId, int wager, string? network, DateTime now)
    {
        PlayerId = playerId;
        Wager = wager;
        Network = network is { Length: > NetworkMaxLength } ? network[..NetworkMaxLength] : network;
        JoinedAt = now;
        SeenAt = now;
    }

    public void StillHere(DateTime now) => SeenAt = now;
}
