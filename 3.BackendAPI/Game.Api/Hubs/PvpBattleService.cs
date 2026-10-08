using Game.Api.Observability;
using Game.Api.Messaging;
using Game.Api.Models;
using Game.Core.Battles;
using Game.Core.Entities;
using Game.Core.Events;
using Game.Core.Services;
using Game.Core.Social;
using Game.Infrastructure.Data;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Game.Api.Options;

namespace Game.Api.Hubs;

/// <summary>
/// Runs PvP battles: starts them when two players are paired, applies moves, settles rewards
/// when a battle ends and pushes every change to both players and anyone watching.
/// </summary>
public class PvpBattleService(
    AppDbContext dbContext,
    PvpMatchmaker matchmaker,
    IBattleRandom random,
    TimeProvider timeProvider,
    MatchOutbox outbox,
    IHubContext<ArenaHub, IArenaClient> hub,
    IHubContext<LobbyHub, ILobbyClient> lobbyHub,
    IOptions<AntiCheatOptions> antiCheat,
    ArenaPulse pulse,
    SessionNotifier notifier,
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

    /// <summary>How many duels <see cref="GetLiveDuelsAsync"/> lists at most.</summary>
    public const int LiveDuelsShown = 20;

    /// <summary>Duels under way that anyone can watch, the newest first.</summary>
    public async Task<IReadOnlyList<DuelWatchView>> GetLiveDuelsAsync()
    {
        var battles = await dbContext.PvpBattles.AsNoTracking()
            .Where(b => b.Status == PvpBattleStatus.InProgress)
            .OrderByDescending(b => b.StartedAt)
            .Take(LiveDuelsShown)
            .ToListAsync();
        if (battles.Count == 0) return [];

        var ids = battles.SelectMany(b => new[] { b.PlayerOneId, b.PlayerTwoId }).Distinct().ToList();
        var players = await dbContext.Players.AsNoTracking().Where(p => ids.Contains(p.Id)).ToDictionaryAsync(p => p.Id);
        return battles
            .Where(b => players.ContainsKey(b.PlayerOneId) && players.ContainsKey(b.PlayerTwoId))
            .Select(b => DuelWatchView.For(b, players[b.PlayerOneId], players[b.PlayerTwoId], Now))
            .ToList();
    }

    /// <summary>One duel as a spectator sees it, finished or not, or null if there is no such duel.</summary>
    public async Task<DuelWatchView?> GetWatchViewAsync(Guid battleId)
    {
        var battle = await dbContext.PvpBattles.AsNoTracking().FirstOrDefaultAsync(b => b.Id == battleId);
        if (battle is null) return null;

        var playerOne = await dbContext.Players.FindAsync(battle.PlayerOneId);
        var playerTwo = await dbContext.Players.FindAsync(battle.PlayerTwoId);
        // A hero who deleted their account takes their name with them.
        return playerOne is null || playerTwo is null ? null : DuelWatchView.For(battle, playerOne, playerTwo, Now);
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

        // A guest is one click away from another guest, so gold can't be funnelled to a real hero through wagers.
        if (wager > 0 && player.IsGuest)
        {
            throw new HubException("Save your hero to duel for gold.");
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

    /// <summary>
    /// Challenges a friend to a friendly duel. The friend is asked in every open browser and has
    /// <see cref="DuelChallenge.OpenFor"/> to accept. Returns null when the challenger's own unfinished
    /// battle was resumed instead (see docs/adr/0037-friends-and-challenges.md).
    /// </summary>
    public async Task<ChallengeSent?> ChallengeAsync(Guid challengerId, Guid friendId, string? network)
    {
        var challenger = await dbContext.Players.FindAsync(challengerId) ?? throw new HubException("Player profile not found.");
        var friendship = Friendship.KeyFor(challengerId, friendId);
        if (!await dbContext.Friendships.AnyAsync(f => f.PairKey == friendship && f.AcceptedAt != null))
        {
            throw new HubException("You can only challenge your friends.");
        }

        if (await GetActiveBattleAsync(challengerId) is { } existing)
        {
            var (you, opponent) = await LoadPlayersAsync(existing, challengerId);
            await hub.Clients.User(challengerId.ToString())
                .MatchFound(new PvpUpdate(PvpBattleView.For(challengerId, existing, you, opponent, Now), null, null));
            return null;
        }

        var friend = await LoadPlayerAsync(friendId);
        if (await GetActiveBattleAsync(friendId) is not null)
        {
            throw new HubException($"{friend.Username} is in a duel right now. Watch it, or challenge them when it's over.");
        }

        await matchmaker.LeaveAsync(challengerId);
        await WithdrawChallengeAsync(challengerId); // one open challenge each

        var challenge = new DuelChallenge(challengerId, friendId, network, Now);
        dbContext.DuelChallenges.Add(challenge);
        try
        {
            await dbContext.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            dbContext.ChangeTracker.Clear();
            throw new HubException("Your challenge couldn't be sent. Please try again.");
        }

        await notifier.ChallengeReceivedAsync(friendId, new ChallengeView(challenge.Id, challenger.Id, challenger.Username, challenger.TitleName,
            challenger.Class, challenger.AvatarUrl, challenger.EquippedFrame, challenger.Rating, challenge.SecondsLeft(Now)));
        return new ChallengeSent(challenge.Id, friend.Username, challenge.SecondsLeft(Now));
    }

    /// <summary>Takes back the challenger's open challenge, if there is one, and tells the friend.</summary>
    public async Task WithdrawChallengeAsync(Guid challengerId)
    {
        if (await dbContext.DuelChallenges.FindAsync(challengerId) is not { } open) return;

        dbContext.DuelChallenges.Remove(open);
        try
        {
            await dbContext.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            dbContext.ChangeTracker.Clear();
            return; // accepted or turned down at the same moment
        }
        await notifier.ChallengeClosedAsync(open.ChallengedId, open.Id);
    }

    /// <summary>Accepts a friend's challenge, which starts the duel for both of them.</summary>
    public async Task AcceptChallengeAsync(Guid playerId, Guid challengeId, string? network)
    {
        var challenge = await TakeChallengeAsync(playerId, challengeId);
        if (challenge.IsExpired(Now))
        {
            throw new HubException("That challenge ran out of time. Challenge them back from your friends list.");
        }

        var challenger = await LoadPlayerAsync(challenge.ChallengerId);
        if (await GetActiveBattleAsync(playerId) is not null)
        {
            await TellChallengerAsync(challenge, $"{(await LoadPlayerAsync(playerId)).Username} is in another duel.");
            throw new HubException("Finish your duel first, then challenge them back.");
        }
        if (await GetActiveBattleAsync(challenge.ChallengerId) is not null)
        {
            throw new HubException($"{challenger.Username} has started another duel. Challenge them when it's over.");
        }

        await matchmaker.LeaveAsync(playerId);
        await matchmaker.LeaveAsync(challenge.ChallengerId);

        // Friendly: no wager. Between one person's own accounts it's practice, as in the lobby.
        var practice = antiCheat.Value.SameNetworkDuelsArePractice && PvpMatchmaker.SameNetwork(challenge.ChallengerNetwork, network);
        var (first, second) = random.Next(0, 2) == 0 ? (playerId, challenge.ChallengerId) : (challenge.ChallengerId, playerId);
        var battle = await StartDuelAsync(first, second, wager: 0, practice);
        await BroadcastAsync(battle, lastTurn: null, rewards: null, (client, update) => client.MatchFound(update));
    }

    /// <summary>Turns down a friend's challenge and tells them.</summary>
    public async Task DeclineChallengeAsync(Guid playerId, Guid challengeId)
    {
        DuelChallenge challenge;
        try
        {
            challenge = await TakeChallengeAsync(playerId, challengeId);
        }
        catch (HubException)
        {
            return; // already gone: nothing to turn down
        }
        await TellChallengerAsync(challenge, $"{(await LoadPlayerAsync(playerId)).Username} can't duel right now.");
    }

    // Removes the challenge so only one answer counts, and closes it in the hero's other tabs.
    private async Task<DuelChallenge> TakeChallengeAsync(Guid playerId, Guid challengeId)
    {
        var challenge = await dbContext.DuelChallenges.FirstOrDefaultAsync(c => c.Id == challengeId && c.ChallengedId == playerId)
            ?? throw new HubException("That challenge is no longer open.");
        dbContext.DuelChallenges.Remove(challenge);
        try
        {
            await dbContext.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            dbContext.ChangeTracker.Clear();
            throw new HubException("That challenge is no longer open.");
        }
        await notifier.ChallengeClosedAsync(playerId, challenge.Id);
        return challenge;
    }

    private Task TellChallengerAsync(DuelChallenge challenge, string reason) =>
        hub.Clients.User(challenge.ChallengerId.ToString()).ChallengeDeclined(reason);

    /// <summary>
    /// Starts a practice duel against the Arena Bot, for a player who doesn't want to wait for someone
    /// else. It pays nothing and moves no ratings, so it can't be farmed (see docs/adr/0032-arena-bot.md).
    /// </summary>
    public async Task StartBotDuelAsync(Guid playerId)
    {
        await matchmaker.LeaveAsync(playerId);

        // Coming back to an unfinished battle resumes it rather than starting another.
        var existing = await GetActiveBattleAsync(playerId);
        if (existing is not null)
        {
            var (you, opponent) = await LoadPlayersAsync(existing, playerId);
            await hub.Clients.User(playerId.ToString())
                .MatchFound(new PvpUpdate(PvpBattleView.For(playerId, existing, you, opponent, Now), null, null));
            return;
        }

        var player = await dbContext.Players.FindAsync(playerId)
            ?? throw new HubException("Player profile not found.");
        await EnsureArenaBotAsync();

        // The player's Duel Elixir is kept for a duel that counts. The coin flip decides who goes first.
        var playerLoadout = new BattleLoadout(player.CardLevel(BattleCard.Fireball), player.CardLevel(BattleCard.HolyShield),
            player.CardLevel(BattleCard.DragonClaw), BonusHp: 0, player.Class);
        var botLoadout = ArenaBot.LoadoutFor(random);
        var battle = random.Next(0, 2) == 0
            ? PvpBattle.Start(playerId, ArenaBot.Id, Now, playerLoadout, botLoadout, practice: true)
            : PvpBattle.Start(ArenaBot.Id, playerId, Now, botLoadout, playerLoadout, practice: true);
        dbContext.PvpBattles.Add(battle);
        await dbContext.SaveChangesAsync();

        await BroadcastAsync(battle, lastTurn: null, rewards: null, (client, update) => client.MatchFound(update));
    }

    // The bot's hero row, made the first time anyone duels it. It has no sign-in account.
    private async Task EnsureArenaBotAsync()
    {
        if (await dbContext.Players.AnyAsync(p => p.Id == ArenaBot.Id)) return;

        var bot = new Player(ArenaBot.Id, ArenaBot.Name, startingGold: 0);
        dbContext.Players.Add(bot);
        try
        {
            await dbContext.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            // Another request made it at the same moment.
            dbContext.Entry(bot).State = EntityState.Detached;
        }
    }

    /// <summary>
    /// Plays the Arena Bot's turn in every duel where it has had its <see cref="ArenaBot.ThinkingTime"/>.
    /// Run every couple of seconds by <see cref="Workers.PvpTurnTimeoutWorker"/> on every replica; the
    /// battle's concurrency token keeps two replicas from playing the same turn. Returns how many it played.
    /// </summary>
    public async Task<int> PlayBotTurnsAsync(CancellationToken cancellationToken = default)
    {
        // A turn started TurnTimeLimit before its deadline, so this is "started at least ThinkingTime ago".
        var dueBy = Now + PvpBattle.TurnTimeLimit - ArenaBot.ThinkingTime;
        var due = await dbContext.PvpBattles
            .Where(b => b.Status == PvpBattleStatus.InProgress && b.ActivePlayerId == ArenaBot.Id && b.TurnDeadline <= dueBy)
            .Select(b => b.Id)
            .ToListAsync(cancellationToken);

        var played = 0;
        foreach (var battleId in due)
        {
            // Loaded one at a time: a failed save clears the change tracker, which would leave the rest unsaved.
            var battle = await dbContext.PvpBattles.FirstOrDefaultAsync(b => b.Id == battleId, cancellationToken);
            if (battle is null || battle.IsFinished || battle.ActivePlayerId != ArenaBot.Id) continue;
            if (battle.IsTurnExpired(Now)) continue; // ExpireOverdueTurnsAsync settles it
            var turn = battle.PlayCard(ArenaBot.Id, ArenaBot.ChooseCard(battle, random), random, Now);
            try
            {
                await SaveAndBroadcastAsync(battle, turn);
                played++;
            }
            catch (HubException)
            {
                // The player forfeited, or another replica played this turn, at the same moment.
            }
        }

        return played;
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

        if (battle.IsFinished)
        {
            // Practice duels count too, so the ops dashboard shows how much the Arena Bot is played.
            GameTelemetry.DuelCompleted(battle.EndReason?.ToString() ?? "Unknown",
                battle.IsParticipant(ArenaBot.Id) ? "bot" : "player", counted: completed is not null);
            pulse.Changed(); // one fewer duel under way, and maybe a new leaderboard
        }

        if (completed is not null)
        {
            var winnerId = completed.WinnerId;
            outbox.Notify();
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
            if (viewer.Id == ArenaBot.Id) continue; // nobody to tell

            var update = new PvpUpdate(
                PvpBattleView.For(viewer.Id, battle, viewer, opponent, Now),
                lastTurn is null ? null : PvpTurnView.For(viewer.Id, lastTurn, playedBy!.Username),
                rewards?.GetValueOrDefault(viewer.Id));
            await send(hub.Clients.User(viewer.Id.ToString()), update);
        }

        // A duel that has only just started has nobody watching yet.
        if (lastTurn is null && !battle.IsFinished) return;

        // Spectators see the duel from neither side. The move is saved and both players are told, so a
        // spectator who misses this one catches up with the next.
        try
        {
            await lobbyHub.Clients.Group(LobbyHub.DuelGroup(battle.Id)).DuelUpdated(new DuelWatchUpdate(
                DuelWatchView.For(battle, playerOne, playerTwo, Now),
                lastTurn is null ? null : PvpTurnView.For(Guid.Empty, lastTurn, playedBy!.Username)));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not tell the spectators of duel {BattleId}.", battle.Id);
        }
    }

    private async Task<(Player You, Player Opponent)> LoadPlayersAsync(PvpBattle battle, Guid playerId) =>
        (await LoadPlayerAsync(playerId), await LoadPlayerAsync(battle.OpponentOf(playerId)));

    private async Task<Player> LoadPlayerAsync(Guid playerId) =>
        await dbContext.Players.FindAsync(playerId) ?? throw new InvalidOperationException($"Player {playerId} not found.");
}
