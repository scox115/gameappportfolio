using Game.Core.Battles;

namespace Game.Api.Models;

public record PlayCardRequest(BattleCard Card);

public record BattleStateResponse(
    Guid Id,
    int PlayerHp,
    int PlayerMaxHp,
    int BossHp,
    int BossMaxHp,
    int BossNextAttack,
    int Turn,
    BattleStatus Status)
{
    public static BattleStateResponse From(PveBattle battle) =>
        new(battle.Id, battle.PlayerHp, PveBattle.PlayerMaxHp, battle.BossHp, PveBattle.BossMaxHp,
            battle.BossNextAttack, battle.Turn, battle.Status);
}

public record BattleTurnResponse(
    int Turn,
    string CardName,
    int DamageDealt,
    int HealthRestored,
    int? BossDamage);

public record BattleRewardResponse(int GoldEarned, int ExperienceEarned, PlayerProfileResponse Player);

public record PlayCardResponse(BattleStateResponse Battle, BattleTurnResponse TurnResult, BattleRewardResponse? Reward);
