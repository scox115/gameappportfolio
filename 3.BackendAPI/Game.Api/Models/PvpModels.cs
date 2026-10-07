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
