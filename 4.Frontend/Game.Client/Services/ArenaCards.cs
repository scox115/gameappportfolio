namespace Game.Client.Services;

public record HandCard(string Id, string Icon, string Label, string Effect, string Risk, string Hint, string Color);

/// <summary>Mirrors the server's card rules (Game.Core BattleCards) for display only.</summary>
public static class ArenaCards
{
    public static readonly HandCard[] Hand =
    [
        new("Fireball", "🔥", "Fireball", "20 DMG", "10% resisted", "Reliable damage you can cast every turn.", "#ea580c"),
        new("HolyShield", "🛡️", "Holy Shield", "15 HEAL + BLOCK", "20% interrupted", "Heals and blocks the next attack against you. Needs a turn to recharge.", "#2563eb"),
        new("DragonClaw", "🐉", "Dragon Claw", "35 DMG", "25% dodged", "Big damage. Needs a turn to recharge.", "#7c3aed")
    ];

    /// <summary>A card's effect line, for a card whose numbers come from the server (Gold Shop upgrades included).</summary>
    public static string EffectOf(int damage, int heal) => damage > 0 ? $"{damage} DMG" : $"{heal} HEAL + BLOCK";
}
