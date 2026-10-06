namespace Game.Api.Hubs;

/// <param name="SameNetwork">The two players connected from the same IP address.</param>
public record PvpPairing(Guid OpponentId, bool SameNetwork);

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

    private sealed record Waiting(Guid PlayerId, string? Network);

    private readonly Dictionary<int, List<Waiting>> _waitingByWager = new();

    /// <summary>
    /// Returns the longest-waiting opponent with the same wager, or null after putting this player
    /// in the queue. Joining again with a different wager moves the player.
    /// </summary>
    /// <param name="network">The player's IP address, or null when it isn't known.</param>
    /// <param name="avoidSameNetwork">Skip opponents on the same network, so wagers can't move gold between one person's accounts.</param>
    public PvpPairing? JoinOrPair(Guid playerId, int wager = 0, string? network = null, bool avoidSameNetwork = false)
    {
        lock (_gate)
        {
            RemoveLocked(playerId);
            var queue = _waitingByWager.TryGetValue(wager, out var existing) ? existing : _waitingByWager[wager] = new();

            var opponent = queue.FirstOrDefault(w => !(avoidSameNetwork && SameNetwork(w.Network, network)));
            if (opponent is not null)
            {
                queue.Remove(opponent);
                return new PvpPairing(opponent.PlayerId, SameNetwork(opponent.Network, network));
            }

            queue.Add(new Waiting(playerId, network));
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
            return _waitingByWager.Values.Any(queue => queue.Any(w => w.PlayerId == playerId));
        }
    }

    private void RemoveLocked(Guid playerId)
    {
        foreach (var queue in _waitingByWager.Values)
        {
            queue.RemoveAll(w => w.PlayerId == playerId);
        }
    }

    // An unknown address never matches, so players are only treated as one network when we can tell.
    private static bool SameNetwork(string? a, string? b) => a is not null && b is not null && a == b;
}
