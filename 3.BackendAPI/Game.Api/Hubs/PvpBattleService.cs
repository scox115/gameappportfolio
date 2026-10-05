using Game.Api.Messaging;
using Game.Api.Models;
using Game.Core.Battles;
using Game.Core.Entities;
using Game.Core.Events;
using Game.Core.Services;
using Game.Infrastructure.Data;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Game.Api.Hubs;

/// <summary>
/// Runs PvP battles: starts them when two players are paired, applies moves, settles rewards
/// when a battle ends and pushes every change to both players.
/// </summary>
public class PvpBattleService(
    AppDbContext dbContext,
    PvpMatchmaker matchmaker,
    IBattleRandom random,
    TimeProvider timeProvider,
    MatchTelemetryPublisher telemetry,
    IHubContext<ArenaHub, IArenaClient> hub,
    ILogger<PvpBattleService> logger)
{
    private DateTime Now => timeProvider.GetUtcNow().UtcDateTime;

    /// <summary>The player's unfinished battle, if any. A battle whose turn timer ran out is settled first.</summary>
    public async Task<PvpBattle?> GetActiveBattleAsync(Guid playerId)
    {
        var battle = await dbContext.PvpBattles.FirstOrDefaultAsync(b =>
            (b.PlayerOneId == playerId || b.PlayerTwoId == playerId) && b.Status == PvpBattleStatus.InProgress);

        if (battle is not null && battle.ExpireTurn(Now))
        {
            try
            {
                await SaveAndBroadcastAsync(battle, lastTurn: null);
            }
            catch (HubException)
            {
                // Something else settled it at the same moment; either way it's over.
            }
            return null;
        }

        return battle;
    }

    public async Task<PvpBattleView?> GetActiveBattleViewAsync(Guid playerId)
    {
        var battle = await GetActiveBattleAsync(playerId);
        if (battle is null) return null;

        var (you, opponent) = await LoadPlayersAsync(battle, playerId);
        return PvpBattleView.For(playerId, battle, you, opponent, Now);
    }

    /// <summary>Returns true when the player is waiting in the lobby, false when a battle started or resumed.</summary>
    public async Task<bool> FindOpponentAsync(Guid playerId)
    {
        if (!await dbContext.Players.AnyAsync(p => p.Id == playerId))
        {
            throw new HubException("Player profile not found.");
        }

        // Coming back to an unfinished battle resumes it rather than starting another.
        var existing = await GetActiveBattleAsync(playerId);
        if (existing is not null)
        {
            var (you, opponent) = await LoadPlayersAsync(existing, playerId);
            await hub.Clients.User(playerId.ToString())
                .MatchFound(new PvpUpdate(PvpBattleView.For(playerId, existing, you, opponent, Now), null, null));
            return false;
        }

        if (matchmaker.JoinOrPair(playerId) is not { } opponentId)
        {
            return true;
        }

        // A coin flip decides who goes first.
        var (first, second) = random.Next(0, 2) == 0 ? (playerId, opponentId) : (opponentId, playerId);
        var battle = PvpBattle.Start(first, second, Now);
        dbContext.PvpBattles.Add(battle);
        await dbContext.SaveChangesAsync();

        await BroadcastAsync(battle, lastTurn: null, rewards: null, (client, update) => client.MatchFound(update));
        return false;
    }

    public async Task PlayCardAsync(Guid playerId, Guid battleId, BattleCard card)
    {
        if (!Enum.IsDefined(card)) throw new HubException("Unknown card.");

        var battle = await LoadInProgressAsync(playerId, battleId);
        if (battle.ActivePlayerId != playerId) throw new HubException("It's not your turn.");
        if (battle.RechargingCardOf(playerId) == card) throw new HubException($"{BattleCards.Get(card).Name} is still recharging.");

        var turn = battle.PlayCard(playerId, card, random, Now);
        await SaveAndBroadcastAsync(battle, turn);
    }

    public async Task ForfeitAsync(Guid playerId, Guid battleId)
    {
        var battle = await LoadInProgressAsync(playerId, battleId);
        battle.Forfeit(playerId, Now);
        await SaveAndBroadcastAsync(battle, lastTurn: null);
    }

    /// <summary>Settles every battle whose active player ran out of time. Returns how many ended.</summary>
    public async Task<int> ExpireOverdueTurnsAsync(CancellationToken cancellationToken = default)
    {
        var now = Now;
        var overdue = await dbContext.PvpBattles
            .Where(b => b.Status == PvpBattleStatus.InProgress && b.TurnDeadline <= now)
            .ToListAsync(cancellationToken);

        var expired = 0;
        foreach (var battle in overdue)
        {
            battle.ExpireTurn(now);
            try
            {
                await SaveAndBroadcastAsync(battle, lastTurn: null);
                expired++;
            }
            catch (HubException)
            {
                // A last-second move or forfeit got there first; that result stands.
            }
        }

        return expired;
    }

    private async Task<PvpBattle> LoadInProgressAsync(Guid playerId, Guid battleId)
    {
        // Another player's battle looks the same as a missing one.
        var battle = await dbContext.PvpBattles.FirstOrDefaultAsync(b =>
            b.Id == battleId && (b.PlayerOneId == playerId || b.PlayerTwoId == playerId));
        if (battle is null) throw new HubException("Battle not found.");
        if (battle.IsFinished) throw new HubException("This battle is already over.");

        if (battle.ExpireTurn(Now))
        {
            await SaveAndBroadcastAsync(battle, lastTurn: null);
            throw new HubException("The turn timer ran out.");
        }

        return battle;
    }

    // Saves the move and, if it ended the battle, the rewards, in one SaveChanges; then tells both players.
    private async Task SaveAndBroadcastAsync(PvpBattle battle, PvpTurnResult? lastTurn)
    {
        Dictionary<Guid, BattleRewardResponse>? rewards = null;
        GameMatch? match = null;
        if (battle.IsFinished)
        {
            (match, rewards) = await SettleAsync(battle);
        }

        try
        {
            await dbContext.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            foreach (var entry in dbContext.ChangeTracker.Entries().ToList())
            {
                entry.State = EntityState.Detached;
            }
            throw new HubException("Another move was played at the same time. Try again.");
        }

        await BroadcastAsync(battle, lastTurn, rewards, (client, update) => client.BattleUpdated(update));

        if (match is not null)
        {
            var winnerId = match.WinnerPlayerId!.Value;
            await telemetry.PublishAsync(new MatchCompletedEvent(match.Id, winnerId, battle.OpponentOf(winnerId)));
            logger.LogInformation("PvP battle {BattleId} won by {WinnerId} ({Reason})", battle.Id, winnerId, battle.EndReason);
        }
    }

    private async Task<(GameMatch Match, Dictionary<Guid, BattleRewardResponse> Rewards)> SettleAsync(PvpBattle battle)
    {
        var winnerId = battle.WinnerId!.Value;
        var winner = await dbContext.Players.FindAsync(winnerId)
            ?? throw new InvalidOperationException($"Player {winnerId} not found.");
        var loserId = battle.OpponentOf(winnerId);
        var loser = await dbContext.Players.FindAsync(loserId)
            ?? throw new InvalidOperationException($"Player {loserId} not found.");

        var winnerGold = winner.Gold;
        var loserGold = loser.Gold;

        var match = new GameMatch(battle.PlayerOneId, battle.PlayerTwoId);
        dbContext.Matches.Add(match);
        new MatchRulesEngine().ProcessMatchWin(match, winner, loser);
        battle.AttachMatch(match.Id);

        var rewards = new Dictionary<Guid, BattleRewardResponse>
        {
            [winnerId] = new(winner.Gold - winnerGold, MatchRulesEngine.WinExperience, PlayerProfileResponse.From(winner)),
            [loserId] = new(loser.Gold - loserGold, MatchRulesEngine.LossExperience, PlayerProfileResponse.From(loser))
        };
        return (match, rewards);
    }

    private async Task BroadcastAsync(
        PvpBattle battle,
        PvpTurnResult? lastTurn,
        Dictionary<Guid, BattleRewardResponse>? rewards,
        Func<IArenaClient, PvpUpdate, Task> send)
    {
        var playerOne = await LoadPlayerAsync(battle.PlayerOneId);
        var playerTwo = await LoadPlayerAsync(battle.PlayerTwoId);
        var playedBy = lastTurn is null ? null : lastTurn.PlayerId == playerOne.Id ? playerOne : playerTwo;

        foreach (var (viewer, opponent) in new[] { (playerOne, playerTwo), (playerTwo, playerOne) })
        {
            var update = new PvpUpdate(
                PvpBattleView.For(viewer.Id, battle, viewer, opponent, Now),
                lastTurn is null ? null : PvpTurnView.For(viewer.Id, lastTurn, playedBy!.Username),
                rewards?.GetValueOrDefault(viewer.Id));
            await send(hub.Clients.User(viewer.Id.ToString()), update);
        }
    }

    private async Task<(Player You, Player Opponent)> LoadPlayersAsync(PvpBattle battle, Guid playerId) =>
        (await LoadPlayerAsync(playerId), await LoadPlayerAsync(battle.OpponentOf(playerId)));

    private async Task<Player> LoadPlayerAsync(Guid playerId) =>
        await dbContext.Players.FindAsync(playerId) ?? throw new InvalidOperationException($"Player {playerId} not found.");
}
