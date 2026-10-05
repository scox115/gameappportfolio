using Game.Api.Auth;
using Game.Core.Battles;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Game.Api.Hubs;

/// <summary>
/// The real-time PvP arena: players join the lobby, get paired, and play their turns here.
/// Results come back to both players through <see cref="IArenaClient"/>.
/// </summary>
[Authorize]
public class ArenaHub(PvpBattleService battles, PvpMatchmaker matchmaker) : Hub<IArenaClient>
{
    public const string Path = "/hubs/arena";

    /// <summary>Joins the lobby. Returns true when waiting, false when a battle started or resumed.</summary>
    public Task<bool> FindOpponent() => battles.FindOpponentAsync(PlayerId);

    public void CancelSearch() => matchmaker.Leave(PlayerId);

    public Task PlayCard(Guid battleId, BattleCard card) => battles.PlayCardAsync(PlayerId, battleId, card);

    public Task Forfeit(Guid battleId) => battles.ForfeitAsync(PlayerId, battleId);

    // Leaving the page takes a player out of the lobby. A battle in progress carries on, and
    // the turn timer settles it if they don't come back.
    public override Task OnDisconnectedAsync(Exception? exception)
    {
        matchmaker.Leave(PlayerId);
        return base.OnDisconnectedAsync(exception);
    }

    private Guid PlayerId => Context.User!.GetPlayerId();
}
