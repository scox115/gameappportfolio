namespace Game.Client.Services;

/// <summary>How the Gold Shop's prestige cosmetics look. They only change styling, never a battle.</summary>
public static class CosmeticStyles
{
    /// <summary>CSS border and glow for an avatar wearing <paramref name="frame"/> (null for none).</summary>
    public static string Frame(string? frame, int width = 3, string plainBorder = "2px solid #475569") => frame switch
    {
        "BronzeFrame" => $"border: {width}px solid #b45309; box-shadow: 0 0 6px rgba(180,83,9,0.7);",
        "SilverFrame" => $"border: {width}px solid #e2e8f0; box-shadow: 0 0 8px rgba(226,232,240,0.8);",
        "GoldFrame" => $"border: {width}px solid #fbbf24; box-shadow: 0 0 12px rgba(251,191,36,0.9);",
        "DragonfireFrame" => $"border: {width}px solid #f97316; box-shadow: 0 0 6px #ef4444, 0 0 16px #f97316, 0 0 26px rgba(250,204,21,0.6);",
        _ => $"border: {plainBorder};"
    };

    /// <summary>A card's background with <paramref name="skin"/> blended into its own colour, so each card stays recognisable.</summary>
    public static string CardBackground(string? skin, string cardColor) => skin switch
    {
        "EmberCards" => $"linear-gradient(135deg, #450a0a 0%, #b91c1c 35%, {cardColor} 75%, #fbbf24 100%)",
        "FrostCards" => $"linear-gradient(135deg, #082f49 0%, #0ea5e9 35%, {cardColor} 75%, #e0f2fe 100%)",
        "VoidCards" => $"linear-gradient(135deg, #020617 0%, #3b0764 40%, {cardColor} 80%, #f0abfc 100%)",
        _ => cardColor
    };

    /// <summary>The cosmetic a shop item sells, as the server names it: "FrameBronze" sells "BronzeFrame".</summary>
    public static string? CosmeticOf(string item) => item switch
    {
        _ when item.StartsWith("Frame") => $"{item["Frame".Length..]}Frame",
        _ when item.StartsWith("CardSkin") => $"{item["CardSkin".Length..]}Cards",
        _ => null
    };
}
