namespace Game.Api.Hubs;

/// <summary>
/// Pairs players who are looking for a battle, first come first served. Players only meet
/// someone who chose the same wager.
/// </summary>
/// <remarks>
/// The queue lives in memory, so it suits a single API instance. Running several instances
/// needs a shared queue (for example Redis or a database table) and an Azure SignalR backplane.
/// </remarks>
public class PvpMatchmaker
{
    private readonly Lock _gate = new();

    // Players are paired as soon as a second one arrives, so at most one is waiting per wager.
    private readonly Dictionary<int, Guid> _waitingByWager = new();

    /// <summary>
    /// Returns the waiting opponent with the same wager to battle, or null after putting this
    /// player in the queue. Joining again with a different wager moves the player.
    /// </summary>
    public Guid? JoinOrPair(Guid playerId, int wager = 0)
    {
        lock (_gate)
        {
            RemoveLocked(playerId);
            if (_waitingByWager.Remove(wager, out var opponentId))
            {
                return opponentId;
            }

            _waitingByWager[wager] = playerId;
            return null;
        }
    }

    public void Leave(Guid playerId)
    {
        lock (_gate)
        {
            RemoveLocked(playerId);
        }
    }

    public bool IsWaiting(Guid playerId)
    {
        lock (_gate)
        {
            return _waitingByWager.ContainsValue(playerId);
        }
    }

    private void RemoveLocked(Guid playerId)
    {
        foreach (var (wager, waiting) in _waitingByWager.ToList())
        {
            if (waiting == playerId) _waitingByWager.Remove(wager);
        }
    }
}
