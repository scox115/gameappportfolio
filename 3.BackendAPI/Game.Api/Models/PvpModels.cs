using Game.Core.Battles;
using Game.Core.Entities;

namespace Game.Api.Models;

/// <param name="Title">The title the player chose in the Gold Shop, such as "the Gladiator".</param>
/// <param name="Rating">The player's PvP rating.</param>
/// <param name="Class">The class the player fights as in this duel.</param>
public record PvpPlayerView(Guid Id, string Username, string? Title, int Rating, string? AvatarUrl, Cosmetic? Frame, int Hp, int MaxHp, bool Shielded,
    HeroClass Class);

/// <summary>A battle as one of its players sees it.</summary>
public record PvpBattleView(
    Guid Id,
    PvpPlayerView You,
    PvpPlayerView Opponent,
    bool YourTurn,
    int Turn,
    DateTime TurnDeadline,
    int TurnSecondsLeft,
    BattleCard? RechargingCard,
    PvpBattleStatus Status,
    bool? YouWon,
    int Wager,
    bool Practice,
    PvpEndReason? EndReason,
    IReadOnlyList<BattleCardResponse> YourCards,
    bool AgainstBot = false)
{
    // TurnSecondsLeft lets the browser count down without trusting its own clock.
    public static PvpBattleView For(Guid viewerId, PvpBattle battle, Player you, Player opponent, DateTime now) =>
        new(battle.Id,
            PlayerView(battle, you),
            PlayerView(battle, opponent),
            !battle.IsFinished && battle.ActivePlayerId == viewerId,
            battle.Turn,
            battle.TurnDeadline,
            battle.IsFinished ? 0 : (int)Math.Ceiling(Math.Max(0, (battle.TurnDeadline - now).TotalSeconds)),
            battle.RechargingCardOf(viewerId),
            battle.Status,
            battle.WinnerId is { } winner ? winner == viewerId : null,
            battle.Wager,
            battle.Practice,
            battle.EndReason,
            BattleCards.All.Select(c => BattleCardResponse.From(battle.CardFor(viewerId, c.Card), battle.LevelOf(viewerId, c.Card))).ToList(),
            battle.IsParticipant(ArenaBot.Id));

    private static PvpPlayerView PlayerView(PvpBattle battle, Player player) =>
        new(player.Id, player.Username, player.TitleName, player.Rating, player.AvatarUrl, player.EquippedFrame, battle.HpOf(player.Id), battle.MaxHpOf(player.Id), battle.IsShielded(player.Id),
            battle.ClassOf(player.Id));
}

/// <summary>The card just played, as one of the players sees it.</summary>
public record PvpTurnView(
    int Turn,
    bool YourCard,
    string PlayerName,
    string CardName,
    bool CardFailed,
    string? CardFailedReason,
    int DamageDealt,
    int HealthRestored,
    bool AttackBlocked)
{
    public static PvpTurnView For(Guid viewerId, PvpTurnResult turn, string playerName) =>
        new(turn.Turn, turn.PlayerId == viewerId, playerName, turn.Card.Name, turn.CardFailed,
            turn.CardFailed ? turn.Card.FailedVerb : null, turn.DamageDealt, turn.HealthRestored, turn.AttackBlocked);
}

/// <summary>Pushed to a player whenever their battle changes. Reward is set once, when it ends.</summary>
public record PvpUpdate(PvpBattleView Battle, PvpTurnView? LastTurn, BattleRewardResponse? Reward);

/// <summary>
/// A duel as a spectator sees it: both heroes side by side and whose turn it is, without either
/// player's cards (see docs/adr/0036-spectating.md).
/// </summary>
/// <param name="ActivePlayerId">Whose turn it is; null once the duel is over.</param>
/// <param name="WinnerId">Who won, once the duel is over.</param>
public record DuelWatchView(
    Guid Id,
    PvpPlayerView PlayerOne,
    PvpPlayerView PlayerTwo,
    Guid? ActivePlayerId,
    int Turn,
    int TurnSecondsLeft,
    PvpBattleStatus Status,
    Guid? WinnerId,
    PvpEndReason? EndReason,
    int Wager,
    bool Practice,
    bool AgainstBot,
    DateTime StartedAt)
{
    public static DuelWatchView For(PvpBattle battle, Player playerOne, Player playerTwo, DateTime now)
    {
        // Player one's view already has both heroes' health, shields and classes; only the cards are theirs alone.
        var asPlayerOne = PvpBattleView.For(playerOne.Id, battle, playerOne, playerTwo, now);
        return new(battle.Id, asPlayerOne.You, asPlayerOne.Opponent,
            battle.IsFinished ? null : battle.ActivePlayerId,
            battle.Turn, asPlayerOne.TurnSecondsLeft, battle.Status, battle.WinnerId, battle.EndReason,
            battle.Wager, battle.Practice, asPlayerOne.AgainstBot, battle.StartedAt);
    }
}

/// <summary>Pushed to a duel's spectators whenever it changes. LastTurn is the card just played, if one was.</summary>
public record DuelWatchUpdate(DuelWatchView Duel, PvpTurnView? LastTurn);

/// <summary>
/// A finished duel to play back: the two heroes as they started (full health, no shields), every card
/// in order with the state after it, and the result (see docs/adr/0038-match-replays.md).
/// </summary>
public record DuelReplayView(
    Guid Id,
    PvpPlayerView PlayerOne,
    PvpPlayerView PlayerTwo,
    IReadOnlyList<DuelReplayMove> Moves,
    Guid? WinnerId,
    PvpEndReason? EndReason,
    int Wager,
    bool Practice,
    bool AgainstBot,
    DateTime StartedAt,
    DateTime? CompletedAt)
{
    public static DuelReplayView For(PvpBattle battle, Player playerOne, Player playerTwo, IReadOnlyList<DuelMove> moves)
    {
        var finished = PvpBattleView.For(playerOne.Id, battle, playerOne, playerTwo, battle.CompletedAt ?? battle.StartedAt);
        var names = new Dictionary<Guid, string> { [playerOne.Id] = playerOne.Username, [playerTwo.Id] = playerTwo.Username };
        return new(battle.Id,
            finished.You with { Hp = finished.You.MaxHp, Shielded = false },
            finished.Opponent with { Hp = finished.Opponent.MaxHp, Shielded = false },
            moves.Select(m => DuelReplayMove.From(m, names[m.PlayerId])).ToList(),
            battle.WinnerId, battle.EndReason, battle.Wager, battle.Practice, finished.AgainstBot, battle.StartedAt, battle.CompletedAt);
    }
}

/// <summary>One card in a replay, and both heroes' health and shields straight after it.</summary>
public record DuelReplayMove(
    int Turn,
    Guid PlayerId,
    string PlayerName,
    string CardName,
    bool CardFailed,
    string? CardFailedReason,
    int DamageDealt,
    int HealthRestored,
    bool AttackBlocked,
    int PlayerOneHp,
    int PlayerTwoHp,
    bool PlayerOneShielded,
    bool PlayerTwoShielded)
{
    public static DuelReplayMove From(DuelMove move, string playerName)
    {
        var card = BattleCards.Get(move.Card);
        return new(move.Turn, move.PlayerId, playerName, card.Name, move.CardFailed, move.CardFailed ? card.FailedVerb : null,
            move.DamageDealt, move.HealthRestored, move.AttackBlocked,
            move.PlayerOneHp, move.PlayerTwoHp, move.PlayerOneShielded, move.PlayerTwoShielded);
    }
}
