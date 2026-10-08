using Game.Api.Auth;
using Game.Api.Features;
using Game.Api.Models;
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
public class ArenaHub(PvpBattleService battles, PvpMatchmaker matchmaker, IFeatureManager features, ArenaPulse pulse) : Hub<IArenaClient>
{
    public const string Path = "/hubs/arena";

    /// <summary>Joins the lobby. Returns true when waiting, false when a battle started or resumed.</summary>
    public async Task<bool> FindOpponent()
    {
        await EnsureDuelsAreOnAsync();
        var waiting = await battles.FindOpponentAsync(PlayerId, network: Network);
        pulse.Changed(); // joined the lobby, or paired and started a duel
        return waiting;
    }

    /// <summary>Joins the lobby staking gold on the duel; only players with the same wager are paired.</summary>
    public async Task<bool> FindWageredOpponent(int wager)
    {
        await EnsureDuelsAreOnAsync();
        var waiting = await battles.FindOpponentAsync(PlayerId, wager, Network);
        pulse.Changed();
        return waiting;
    }

    /// <summary>Starts a practice duel against the Arena Bot instead of waiting for another player.</summary>
    public async Task DuelBot()
    {
        await EnsureDuelsAreOnAsync();
        await battles.StartBotDuelAsync(PlayerId);
        pulse.Changed();
    }

    // Switching duels off stops new ones; duels already under way play out.
    private async Task EnsureDuelsAreOnAsync()
    {
        if (!await features.IsEnabledAsync(GameFeatures.Duels))
        {
            throw new HubException("Duels are switched off for now. Please try again later.");
        }
    }

    /// <summary>Challenges a friend to a friendly duel. Returns null when an unfinished battle was resumed instead.</summary>
    public async Task<ChallengeSent?> ChallengeFriend(Guid friendId)
    {
        await EnsureDuelsAreOnAsync();
        var sent = await battles.ChallengeAsync(PlayerId, friendId, Network);
        pulse.Changed(); // out of the lobby, if they were in it
        return sent;
    }

    public Task WithdrawChallenge() => battles.WithdrawChallengeAsync(PlayerId);

    /// <summary>Accepts a friend's challenge; MatchFound brings the duel to both of them.</summary>
    public async Task AcceptChallenge(Guid challengeId)
    {
        await EnsureDuelsAreOnAsync();
        await battles.AcceptChallengeAsync(PlayerId, challengeId, Network);
        pulse.Changed();
    }

    public Task DeclineChallenge(Guid challengeId) => battles.DeclineChallengeAsync(PlayerId, challengeId);

    public async Task CancelSearch()
    {
        await matchmaker.LeaveAsync(PlayerId);
        pulse.Changed();
    }

    public Task PlayCard(Guid battleId, BattleCard card) => battles.PlayCardAsync(PlayerId, battleId, card);

    public Task Forfeit(Guid battleId) => battles.ForfeitAsync(PlayerId, battleId);

    // Leaving the page takes a player out of the lobby. A battle in progress carries on, and
    // the turn timer settles it if they don't come back.
    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        await matchmaker.LeaveAsync(PlayerId);
        await battles.WithdrawChallengeAsync(PlayerId); // nobody is left waiting for the answer
        pulse.Changed();
        await base.OnDisconnectedAsync(exception);
    }

    private Guid PlayerId => Context.User!.GetPlayerId();

    // The player's IP address (behind a proxy, from X-Forwarded-For; see UseForwardedHeaders).
    private string? Network => Context.GetHttpContext()?.Connection.RemoteIpAddress?.ToString();
}
