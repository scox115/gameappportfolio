using Game.Api.Models;
using Game.Core.Battles;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Game.Api.Hubs;

/// <summary>What's happening in the arena right now, as shown in the lobby.</summary>
/// <param name="PlayersOnline">Signed-in heroes (guests included) with the game open.</param>
/// <param name="WaitingForDuel">Heroes in the duel lobby waiting for an opponent.</param>
/// <param name="DuelsUnderWay">Duels being played, Arena Bot duels included.</param>
/// <param name="LeaderboardUpdatedAt">When the latest match that can move the leaderboards finished; a change means they're worth reloading.</param>
public record ArenaPulseView(int PlayersOnline, int WaitingForDuel, int DuelsUnderWay, DateTime? LeaderboardUpdatedAt);

public interface ILobbyClient
{
    Task PulseUpdated(ArenaPulseView pulse);

    /// <summary>A card was played, a turn timed out or someone forfeited in a duel this connection is watching.</summary>
    Task DuelUpdated(DuelWatchUpdate update);
}

/// <summary>
/// Streams <see cref="ArenaPulseView"/> to every open copy of the game, signed in or not, so a visitor
/// can see the arena is alive before they play, and lets anyone watch a duel under way turn by turn
/// (see docs/adr/0036-spectating.md).
/// </summary>
[AllowAnonymous]
public class LobbyHub(ArenaPulse pulse, PvpBattleService battles) : Hub<ILobbyClient>
{
    public const string Path = "/hubs/lobby";

    /// <summary>The SignalR group of everyone watching one duel.</summary>
    public static string DuelGroup(Guid duelId) => $"duel-{duelId:N}";

    /// <summary>
    /// Starts sending this connection the duel's moves. Returns the duel as it stands, or null if there
    /// is no such duel. A finished duel is returned too, so a spectator who arrives late sees the result.
    /// </summary>
    public async Task<DuelWatchView?> WatchDuel(Guid duelId)
    {
        // Joined first, so a move made while the duel is read isn't missed.
        await Groups.AddToGroupAsync(Context.ConnectionId, DuelGroup(duelId), Context.ConnectionAborted);
        var duel = await battles.GetWatchViewAsync(duelId);
        if (duel is null || duel.Status != PvpBattleStatus.InProgress)
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, DuelGroup(duelId), Context.ConnectionAborted);
        }
        return duel;
    }

    /// <summary>Stops sending this connection the duel's moves.</summary>
    public Task StopWatching(Guid duelId) =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, DuelGroup(duelId), Context.ConnectionAborted);

    // Later changes are broadcast; a new connection gets the current numbers straight away.
    public override async Task OnConnectedAsync()
    {
        await Clients.Caller.PulseUpdated(await pulse.ReadAsync(Context.ConnectionAborted));
        await base.OnConnectedAsync();
    }
}
