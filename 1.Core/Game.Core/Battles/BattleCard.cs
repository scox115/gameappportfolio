namespace Game.Core.Battles;

public enum BattleCard
{
    Fireball,
    HolyShield,
    DragonClaw
}

public record BattleCardDefinition(BattleCard Card, string Name, int Damage, int Heal);

public static class BattleCards
{
    private static readonly IReadOnlyDictionary<BattleCard, BattleCardDefinition> Definitions =
        new Dictionary<BattleCard, BattleCardDefinition>
        {
            [BattleCard.Fireball] = new(BattleCard.Fireball, "Fireball", Damage: 20, Heal: 0),
            [BattleCard.HolyShield] = new(BattleCard.HolyShield, "Holy Shield", Damage: 0, Heal: 15),
            [BattleCard.DragonClaw] = new(BattleCard.DragonClaw, "Dragon Claw", Damage: 35, Heal: 0)
        };

    public static IEnumerable<BattleCardDefinition> All => Definitions.Values;

    public static BattleCardDefinition Get(BattleCard card) =>
        Definitions.TryGetValue(card, out var definition)
            ? definition
            : throw new ArgumentOutOfRangeException(nameof(card), card, "Unknown battle card.");
}
