using Game.Core.Battles;

namespace Game.Api.Models;

public record PlayCardRequest(BattleCard Card);

public record BattleStateResponse(
    Guid Id,
    int PlayerHp,
    int PlayerMaxHp,
    int BossHp,
    int BossMaxHp,
    bool BossEnraged,
    BossMove BossNextMove,
    string BossNextMoveName,
    int BossNextAttack,
    BattleCard? RechargingCard,
    int Turn,
    BattleStatus Status,
    IReadOnlyList<BattleCardResponse> Cards)
{
    public static BattleStateResponse From(PveBattle battle) =>
        new(battle.Id, battle.PlayerHp, battle.PlayerMaxHp, battle.BossHp, PveBattle.BossMaxHp, battle.IsEnraged,
            battle.BossNextMove, BossMoves.NameOf(battle.BossNextMove), battle.BossNextAttack,
            battle.RechargingCard, battle.Turn, battle.Status,
            BattleCards.All.Select(c => BattleCardResponse.From(battle.CardFor(c.Card), battle.LevelOf(c.Card))).ToList());
}

/// <summary>A card as the player holds it in this battle, with any Gold Shop upgrade applied.</summary>
public record BattleCardResponse(BattleCard Card, string Name, int Level, int Damage, int Heal, int FailChance)
{
    public static BattleCardResponse From(BattleCardDefinition card, int level) =>
        new(card.Card, card.Name, level, card.Damage, card.Heal, card.FailChance);
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
