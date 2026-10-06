namespace Game.Client.Services;

/// <summary>An icon for each playable class. The classes themselves come from GET /api/v1/classes.</summary>
public static class HeroClassIcons
{
    public static string For(string? heroClass) => heroClass switch
    {
        "Sorcerer" => "🔮",
        "Paladin" => "⚜️",
        "Ranger" => "🏹",
        _ => "❔"
    };

    /// <summary>The card a class boosts, matching HeroClasses on the server, which applies the boost.</summary>
    public static string? SignatureCard(string? heroClass) => heroClass switch
    {
        "Sorcerer" => "Fireball",
        "Paladin" => "HolyShield",
        "Ranger" => "DragonClaw",
        _ => null
    };
}
