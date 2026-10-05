using Game.Core.Battles;

namespace Game.Api.Models;

public record PlayCardRequest(BattleCard Card);

public record BattleStateResponse(
    Guid Id,
    int PlayerHp,
    int PlayerMaxHp,
    int BossHp,
    int BossMaxHp,
    BossMove BossNextMove,
    string BossNextMoveName,
    int BossNextAttack,
    BattleCard? RechargingCard,
    int Turn,
    BattleStatus Status)
{
    public static BattleStateResponse From(PveBattle battle) =>
        new(battle.Id, battle.PlayerHp, PveBattle.PlayerMaxHp, battle.BossHp, PveBattle.BossMaxHp,
            battle.BossNextMove, BossMoves.NameOf(battle.BossNextMove), battle.BossNextAttack,
            battle.RechargingCard, battle.Turn, battle.Status);
}

public record BattleTurnResponse(
    int Turn,
    string CardName,
    bool CardFailed,
    string? CardFailedReason,
    int DamageDealt,
    int HealthRestored,
    string? BossMoveName,
    int? BossDamage,
    bool AttackBlocked,
    int BossHealed)
{
    public static BattleTurnResponse From(BattleTurnResult turn) =>
        new(turn.Turn, turn.Card.Name, turn.CardFailed, turn.CardFailed ? turn.Card.FailedVerb : null,
            turn.DamageDealt, turn.HealthRestored,
            turn.BossMove is { } move ? BossMoves.NameOf(move) : null,
            turn.BossDamage, turn.AttackBlocked, turn.BossHealed);
}

public record BattleRewardResponse(int GoldEarned, int ExperienceEarned, PlayerProfileResponse Player);

public record PlayCardResponse(BattleStateResponse Battle, BattleTurnResponse TurnResult, BattleRewardResponse? Reward);
