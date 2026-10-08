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
}

/// <summary>
/// Streams <see cref="ArenaPulseView"/> to every open copy of the game, signed in or not, so a visitor
/// can see the arena is alive before they play. It only sends: there is nothing to call.
/// </summary>
[AllowAnonymous]
public class LobbyHub(ArenaPulse pulse) : Hub<ILobbyClient>
{
    public const string Path = "/hubs/lobby";

    // Later changes are broadcast; a new connection gets the current numbers straight away.
    public override async Task OnConnectedAsync()
    {
        await Clients.Caller.PulseUpdated(await pulse.ReadAsync(Context.ConnectionAborted));
        await base.OnConnectedAsync();
    }
}
