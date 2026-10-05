namespace Game.Core.Battles;

public enum BattleCard
{
    Fireball,
    HolyShield,
    DragonClaw
}

/// <summary>
/// A card's effect. <paramref name="FailChance"/> is the percent chance the card is
/// <paramref name="FailedVerb"/> (the boss resists, dodges or interrupts it) and does nothing.
/// A card that <paramref name="NeedsRecharge"/> can't be played two turns in a row.
/// A <paramref name="BlocksAttack"/> card stops the boss's attack this turn when it lands.
/// </summary>
public record BattleCardDefinition(
    BattleCard Card,
    string Name,
    int Damage,
    int Heal,
    int FailChance,
    string FailedVerb,
    bool NeedsRecharge,
    bool BlocksAttack);

public static class BattleCards
{
    /// <summary>Cards start at level 1 and can be upgraded in the Gold Shop up to this level.</summary>
    public const int MaxLevel = 3;

    /// <summary>Extra damage (or healing, for Holy Shield) each level above 1 adds.</summary>
    public const int BonusPerLevel = 5;

    private static readonly IReadOnlyDictionary<BattleCard, BattleCardDefinition> Definitions =
        new Dictionary<BattleCard, BattleCardDefinition>
        {
            [BattleCard.Fireball] = new(BattleCard.Fireball, "Fireball",
                Damage: 20, Heal: 0, FailChance: 10, FailedVerb: "resisted", NeedsRecharge: false, BlocksAttack: false),
            [BattleCard.HolyShield] = new(BattleCard.HolyShield, "Holy Shield",
                Damage: 0, Heal: 15, FailChance: 20, FailedVerb: "interrupted", NeedsRecharge: true, BlocksAttack: true),
            [BattleCard.DragonClaw] = new(BattleCard.DragonClaw, "Dragon Claw",
                Damage: 35, Heal: 0, FailChance: 25, FailedVerb: "dodged", NeedsRecharge: true, BlocksAttack: false)
        };

    public static IEnumerable<BattleCardDefinition> All => Definitions.Values;

    /// <summary>The card at an upgrade level. Only its damage or healing grows; the risk stays the same.</summary>
    public static BattleCardDefinition AtLevel(BattleCard card, int level)
    {
        if (level is < 1 or > MaxLevel) throw new ArgumentOutOfRangeException(nameof(level), level, $"Card levels run from 1 to {MaxLevel}.");

        var definition = Get(card);
        var bonus = (level - 1) * BonusPerLevel;
        return definition with
        {
            Damage = definition.Damage > 0 ? definition.Damage + bonus : 0,
            Heal = definition.Heal > 0 ? definition.Heal + bonus : 0
        };
    }

    public static BattleCardDefinition Get(BattleCard card) =>
        Definitions.TryGetValue(card, out var definition)
            ? definition
            : throw new ArgumentOutOfRangeException(nameof(card), card, "Unknown battle card.");
}
