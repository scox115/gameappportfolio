using Game.Api.Observability;
using Game.Api.Messaging;
using Game.Api.Models;
using Game.Core.Battles;
using Game.Core.Entities;
using Game.Core.Events;
using Game.Core.Services;
using Game.Infrastructure.Data;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Game.Api.Options;

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
    MatchOutbox outbox,
    IHubContext<ArenaHub, IArenaClient> hub,
    IOptions<AntiCheatOptions> antiCheat,
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
    /// <param name="network">The player's IP address, used to keep a player's own accounts from farming each other.</param>
    public async Task<bool> FindOpponentAsync(Guid playerId, int wager = 0, string? network = null)
    {
        if (!DuelWagers.IsAllowed(wager))
        {
            throw new HubException($"Wagers can be {string.Join(", ", DuelWagers.Stakes.Where(s => s > 0))} gold.");
        }

        var player = await dbContext.Players.FindAsync(playerId)
            ?? throw new HubException("Player profile not found.");

        // Coming back to an unfinished battle resumes it rather than starting another.
        var existing = await GetActiveBattleAsync(playerId);
        if (existing is not null)
        {
            var (you, opponent) = await LoadPlayersAsync(existing, playerId);
            await hub.Clients.User(playerId.ToString())
                .MatchFound(new PvpUpdate(PvpBattleView.For(playerId, existing, you, opponent, Now), null, null));
            return false;
        }

        if (player.Gold < wager)
        {
            throw new HubException($"You need {wager} gold to wager that, and you have {player.Gold}.");
        }

        // The waiting player may have spent their gold since joining; if so they leave the lobby
        // and this player takes the next opponent, or waits.
        var practiceOnSameNetwork = antiCheat.Value.SameNetworkDuelsArePractice;
        while (await matchmaker.JoinOrPairAsync(playerId, wager, network, avoidSameNetwork: wager > 0 && practiceOnSameNetwork) is { } pairing)
        {
            var opponentId = pairing.OpponentId;
            var opponent = await LoadPlayerAsync(opponentId);
            if (opponent.Gold < wager)
            {
                await hub.Clients.User(opponentId.ToString())
                    .SearchCancelled($"You no longer have the {wager} gold you wagered, so you've left the lobby.");
                continue;
            }

            // A coin flip decides who goes first.
            var (first, second) = random.Next(0, 2) == 0 ? (playerId, opponentId) : (opponentId, playerId);
            var battle = await StartDuelAsync(first, second, wager, practice: pairing.SameNetwork && practiceOnSameNetwork);

            await BroadcastAsync(battle, lastTurn: null, rewards: null, (client, update) => client.MatchFound(update));
            return false;
        }

        return true;
    }

    // Each player brings their upgraded cards, drinks a Duel Elixir if they have one and pays their
    // stake. A shop purchase saved at the same moment changes the player row, so reload and try again.
    private async Task<PvpBattle> StartDuelAsync(Guid first, Guid second, int wager, bool practice)
    {
        for (var attempt = 1; ; attempt++)
        {
            var firstPlayer = await LoadPlayerAsync(first);
            var secondPlayer = await LoadPlayerAsync(second);
            try
            {
                firstPlayer.StakeWager(wager);
                secondPlayer.StakeWager(wager);
            }
            catch (InvalidOperationException ex)
            {
                // Both players have left the queue; tell each one, since only the caller gets the error.
                await hub.Clients.Users(first.ToString(), second.ToString())
                    .SearchCancelled("The duel couldn't start because a player no longer had the gold for the wager. Try again.");
                throw new HubException(ex.Message);
            }

            var battle = PvpBattle.Start(first, second, Now, firstPlayer.TakeLoadoutForDuel(), secondPlayer.TakeLoadoutForDuel(), wager, practice);
            dbContext.PvpBattles.Add(battle);
            try
            {
                await dbContext.SaveChangesAsync();
                return battle;
            }
            catch (DbUpdateConcurrencyException) when (attempt < 3)
            {
                foreach (var entry in dbContext.ChangeTracker.Entries().ToList())
                {
                    entry.State = EntityState.Detached;
                }
            }
        }
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

    // Saves the move and, if it ended the battle, the rewards and the match event, in one SaveChanges;
    // then tells both players.
    private async Task SaveAndBroadcastAsync(PvpBattle battle, PvpTurnResult? lastTurn)
    {
        Dictionary<Guid, BattleRewardResponse>? rewards = null;
        MatchCompletedEvent? completed = null;
        if (battle.IsFinished)
        {
            (completed, rewards) = await SettleAsync(battle);
            if (completed is not null) outbox.Add(dbContext, completed);
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

        if (completed is not null)
        {
            var winnerId = completed.WinnerId;
            outbox.Notify();
            GameTelemetry.BattleCompleted("pvp", battle.EndReason?.ToString() ?? "Unknown");
            logger.LogInformation("PvP battle {BattleId} won by {WinnerId} ({Reason})", battle.Id, winnerId, battle.EndReason);
        }
    }

    // Returns the event to save with the rewards, or null for a duel that didn't count.
    private async Task<(MatchCompletedEvent? Completed, Dictionary<Guid, BattleRewardResponse> Rewards)> SettleAsync(PvpBattle battle)
    {
        var winnerId = battle.WinnerId!.Value;
        var winner = await dbContext.Players.FindAsync(winnerId)
            ?? throw new InvalidOperationException($"Player {winnerId} not found.");
        var loserId = battle.OpponentOf(winnerId);
        var loser = await dbContext.Players.FindAsync(loserId)
            ?? throw new InvalidOperationException($"Player {loserId} not found.");

        // Practice duels, very early forfeits and too many duels against the same opponent don't
        // count: nobody is paid, ratings stay put and both stakes go back.
        var startOfToday = Now.Date;
        var rewardedToday = await dbContext.PvpBattles.CountAsync(b =>
            b.MatchId != null && b.CompletedAt >= startOfToday &&
            ((b.PlayerOneId == winnerId && b.PlayerTwoId == loserId) || (b.PlayerOneId == loserId && b.PlayerTwoId == winnerId)));
        if (DuelRewardRules.NoRewardReason(battle, rewardedToday) is { } reason)
        {
            winner.RefundWager(battle.Wager);
            loser.RefundWager(battle.Wager);
            return (null, new Dictionary<Guid, BattleRewardResponse>
            {
                [winnerId] = BattleRewardResponse.NotCounted(winner, reason),
                [loserId] = BattleRewardResponse.NotCounted(loser, reason)
            });
        }

        var match = new GameMatch(battle.PlayerOneId, battle.PlayerTwoId);
        dbContext.Matches.Add(match);
        var settlement = new MatchRulesEngine(timeProvider).ProcessMatchWin(match, winner, loser, battle.Wager,
            battle.ClassOf(winnerId), battle.ClassOf(loserId));
        battle.AttachMatch(match.Id);

        var rewards = new Dictionary<Guid, BattleRewardResponse>
        {
            [winnerId] = BattleRewardResponse.From(settlement.Winner, winner, isDuel: true),
            [loserId] = BattleRewardResponse.From(settlement.Loser, loser, isDuel: true)
        };
        var completed = new MatchCompletedEvent(match.Id, winnerId, loserId)
        {
            OccurredAt = Now,
            Kind = MatchKind.Duel,
            EndReason = battle.EndReason,
            Turns = battle.MovesPlayed,
            Participants =
            [
                Participant(winner, won: true, settlement.Winner),
                Participant(loser, won: false, settlement.Loser)
            ]
        };
        return (completed, rewards);

        MatchParticipant Participant(Player hero, bool won, BattleReward reward) =>
            new(hero.Id, hero.Username, battle.ClassOf(hero.Id), won, reward.Gold, reward.Experience, reward.RatingChange, reward.WagerResult);
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
