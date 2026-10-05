using Game.Api.Models;

namespace Game.Api.Hubs;

/// <summary>Messages the server pushes to players' browsers.</summary>
public interface IArenaClient
{
    /// <summary>An opponent was found (or an unfinished battle was resumed).</summary>
    Task MatchFound(PvpUpdate update);

    /// <summary>A card was played, a turn timed out or someone forfeited.</summary>
    Task BattleUpdated(PvpUpdate update);
}
