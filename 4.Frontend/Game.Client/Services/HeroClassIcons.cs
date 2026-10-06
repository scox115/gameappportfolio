namespace Game.Client.Services;

/// <summary>An icon for each playable class. The classes themselves come from GET /api/classes.</summary>
public static class HeroClassIcons
{
    public static string For(string? heroClass) => heroClass switch
    {
        "Sorcerer" => "🔮",
        "Paladin" => "⚜️",
        "Ranger" => "🏹",
        _ => "❔"
    };
}
