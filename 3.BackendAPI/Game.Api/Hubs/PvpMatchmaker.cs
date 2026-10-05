namespace Game.Api.Hubs;

/// <summary>
/// Pairs players who are looking for a battle, first come first served.
/// </summary>
/// <remarks>
/// The queue lives in memory, so it suits a single API instance. Running several instances
/// needs a shared queue (for example Redis or a database table) and an Azure SignalR backplane.
/// </remarks>
public class PvpMatchmaker
{
    private readonly Lock _gate = new();

    // Players are paired as soon as a second one arrives, so at most one is ever waiting.
    private Guid? _waitingPlayerId;

    /// <summary>
    /// Returns the waiting opponent to battle, or null after putting this player in the queue.
    /// </summary>
    public Guid? JoinOrPair(Guid playerId)
    {
        lock (_gate)
        {
            if (_waitingPlayerId is { } opponentId && opponentId != playerId)
            {
                _waitingPlayerId = null;
                return opponentId;
            }

            _waitingPlayerId = playerId;
            return null;
        }
    }

    public void Leave(Guid playerId)
    {
        lock (_gate)
        {
            if (_waitingPlayerId == playerId)
            {
                _waitingPlayerId = null;
            }
        }
    }

    public bool IsWaiting(Guid playerId)
    {
        lock (_gate)
        {
            return _waitingPlayerId == playerId;
        }
    }
}
