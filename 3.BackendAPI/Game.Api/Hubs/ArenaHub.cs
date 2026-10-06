using Game.Api.Auth;
using Game.Api.Features;
using Game.Core.Battles;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.FeatureManagement;

namespace Game.Api.Hubs;

/// <summary>
/// The real-time PvP arena: players join the lobby, get paired, and play their turns here.
/// Results come back to both players through <see cref="IArenaClient"/>.
/// </summary>
[Authorize]
public class ArenaHub(PvpBattleService battles, PvpMatchmaker matchmaker, IFeatureManager features) : Hub<IArenaClient>
{
    public const string Path = "/hubs/arena";

    /// <summary>Joins the lobby. Returns true when waiting, false when a battle started or resumed.</summary>
    public async Task<bool> FindOpponent()
    {
        await EnsureDuelsAreOnAsync();
        return await battles.FindOpponentAsync(PlayerId, network: Network);
    }

    /// <summary>Joins the lobby staking gold on the duel; only players with the same wager are paired.</summary>
    public async Task<bool> FindWageredOpponent(int wager)
    {
        await EnsureDuelsAreOnAsync();
        return await battles.FindOpponentAsync(PlayerId, wager, Network);
    }

    // Switching duels off stops new ones; duels already under way play out.
    private async Task EnsureDuelsAreOnAsync()
    {
        if (!await features.IsEnabledAsync(GameFeatures.Duels))
        {
            throw new HubException("Duels are switched off for now. Please try again later.");
        }
    }

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

    // The player's IP address (behind a proxy, from X-Forwarded-For; see UseForwardedHeaders).
    private string? Network => Context.GetHttpContext()?.Connection.RemoteIpAddress?.ToString();
}
