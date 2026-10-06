namespace Game.Core.Battles;

public enum HeroClass
{
    Sorcerer,
    Paladin,
    Ranger
}

/// <summary>How a class improves one of its cards.</summary>
/// <param name="FailChanceCut">Percentage points taken off the card's chance to fail.</param>
public record CardBoost(int Damage = 0, int Heal = 0, int FailChanceCut = 0);

/// <summary>
/// A playable class. Every class holds the same three cards; each makes one of them its
/// signature card and may add extra health. Tuned with the boss and duel simulators so no
/// class wins much more than another.
/// </summary>
public record HeroClassDefinition(
    HeroClass Class,
    string Name,
    string Description,
    int BonusHp,
    BattleCard SignatureCard,
    CardBoost Boost);

public static class HeroClasses
{
    /// <summary>Gold to switch to another class after sign-up.</summary>
    public const int ChangePrice = 300;

    private static readonly IReadOnlyDictionary<HeroClass, HeroClassDefinition> Definitions =
        new Dictionary<HeroClass, HeroClassDefinition>
        {
            [HeroClass.Sorcerer] = new(HeroClass.Sorcerer, "Sorcerer",
                "Arcane Mastery: Fireball deals +2 damage and is resisted half as often (5%).",
                BonusHp: 0, BattleCard.Fireball, new CardBoost(Damage: 2, FailChanceCut: 5)),
            [HeroClass.Paladin] = new(HeroClass.Paladin, "Paladin",
                "Blessed: Holy Shield heals 5 more and is interrupted less often (15%).",
                BonusHp: 0, BattleCard.HolyShield, new CardBoost(Heal: 5, FailChanceCut: 5)),
            [HeroClass.Ranger] = new(HeroClass.Ranger, "Ranger",
                "Keen Eye: Dragon Claw is dodged less often (20%).",
                BonusHp: 0, BattleCard.DragonClaw, new CardBoost(FailChanceCut: 5))
        };

    public static IEnumerable<HeroClassDefinition> All => Definitions.Values;

    public static HeroClassDefinition Get(HeroClass heroClass) =>
        Definitions.TryGetValue(heroClass, out var definition)
            ? definition
            : throw new ArgumentOutOfRangeException(nameof(heroClass), heroClass, "Unknown class.");

    /// <summary>The card at an upgrade level, with the class's boost if it's the signature card.</summary>
    public static BattleCardDefinition CardFor(HeroClass heroClass, BattleCard card, int level)
    {
        var definition = BattleCards.AtLevel(card, level);
        var hero = Get(heroClass);
        if (hero.SignatureCard != card) return definition;

        return definition with
        {
            Damage = definition.Damage > 0 ? definition.Damage + hero.Boost.Damage : 0,
            Heal = definition.Heal > 0 ? definition.Heal + hero.Boost.Heal : 0,
            FailChance = Math.Max(0, definition.FailChance - hero.Boost.FailChanceCut)
        };
    }
}
