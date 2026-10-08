namespace Game.Core.Battles;

/// <summary>
/// One open browser of a signed-in player, so the lobby can show how many heroes are online. Like the
/// duel lobby it is a database table, so every API replica counts the same players
/// (see docs/adr/0034-live-lobby.md).
/// </summary>
public class OnlinePresence
{
    public const int ConnectionIdMaxLength = 128;

    /// <summary>How long a row counts without a heartbeat, so a replica that stopped stops counting its players.</summary>
    public static readonly TimeSpan StaleAfter = PvpLobbyEntry.StaleAfter;

    /// <summary>The SignalR connection: a player with two tabs open has two rows but counts once.</summary>
    public string ConnectionId { get; private set; } = string.Empty;

    public Guid PlayerId { get; private set; }

    /// <summary>When the replica holding the connection last vouched that it is still open.</summary>
    public DateTime SeenAt { get; private set; }

    private OnlinePresence() { }

    public OnlinePresence(string connectionId, Guid playerId, DateTime now)
    {
        ConnectionId = connectionId;
        PlayerId = playerId;
        SeenAt = now;
    }

    public void StillHere(DateTime now) => SeenAt = now;
}
