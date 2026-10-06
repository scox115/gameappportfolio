namespace Game.Core.Battles;

public enum BossDifficulty
{
    Normal,

    /// <summary>A tougher boss for players whose cards are fully upgraded.</summary>
    Heroic
}

/// <summary>
/// The boss's numbers for one difficulty: its health, how hard it hits and how often it uses
/// each move. The boss announces each move a turn ahead, whatever the difficulty.
/// </summary>
/// <param name="OpeningAttack">Damage of the boss's first, announced move.</param>
/// <param name="AttackBase">Each later move hits for this, plus <paramref name="AttackGrowthPerTurn"/> per turn and a random 0 to <paramref name="AttackVariance"/>.</param>
/// <param name="EnrageBelowPercent">Below this share of its health the boss is enraged.</param>
/// <param name="EnrageDamagePercent">An enraged boss's moves deal this percent of their damage.</param>
/// <param name="CrushingBlowChance">Chance out of 100 of a Crushing Blow; Slash takes whatever Crushing Blow and Life Drain leave.</param>
public record BossProfile(
    BossDifficulty Difficulty,
    string Name,
    int MaxHp,
    int OpeningAttack,
    int AttackBase,
    int AttackGrowthPerTurn,
    int AttackVariance,
    int EnrageBelowPercent,
    int EnrageDamagePercent,
    int CrushingBlowChance,
    int LifeDrainChance)
{
    public static readonly BossProfile Normal = new(BossDifficulty.Normal, "The Shadow Overlord",
        PveBattle.BossMaxHp, PveBattle.OpeningBossAttack, PveBattle.BossAttackBase, PveBattle.BossAttackGrowthPerTurn,
        PveBattle.BossAttackVariance, PveBattle.EnrageBelowPercent, PveBattle.EnrageDamagePercent,
        PveBattle.CrushingBlowChance, PveBattle.LifeDrainChance);

    public static readonly BossProfile Heroic = new(BossDifficulty.Heroic, "The Heroic Shadow Overlord",
        MaxHp: 200, OpeningAttack: 18, AttackBase: 10, AttackGrowthPerTurn: 2, AttackVariance: 5,
        EnrageBelowPercent: 50, EnrageDamagePercent: 150, CrushingBlowChance: 30, LifeDrainChance: 25);

    public static BossProfile For(BossDifficulty difficulty) => difficulty switch
    {
        BossDifficulty.Normal => Normal,
        BossDifficulty.Heroic => Heroic,
        _ => throw new ArgumentOutOfRangeException(nameof(difficulty), difficulty, "Unknown boss difficulty.")
    };
}
