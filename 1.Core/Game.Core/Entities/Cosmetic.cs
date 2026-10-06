namespace Game.Core.Entities;

/// <summary>Where a cosmetic shows. A player wears at most one of each kind.</summary>
public enum CosmeticKind
{
    /// <summary>A border around the player's avatar, seen on the leaderboard and by duel opponents.</summary>
    AvatarFrame,

    /// <summary>How the player's battle cards look.</summary>
    CardSkin
}

/// <summary>Prestige items bought in the Gold Shop. They only change how things look, never a battle.</summary>
public enum Cosmetic
{
    BronzeFrame,
    SilverFrame,
    GoldFrame,
    DragonfireFrame,
    EmberCards,
    FrostCards,
    VoidCards
}

public record CosmeticDefinition(Cosmetic Cosmetic, CosmeticKind Kind, string Name, string Description, int Price);

public static class CosmeticCatalog
{
    private static readonly IReadOnlyDictionary<Cosmetic, CosmeticDefinition> Definitions =
        new Dictionary<Cosmetic, CosmeticDefinition>
        {
            [Cosmetic.BronzeFrame] = new(Cosmetic.BronzeFrame, CosmeticKind.AvatarFrame, "Bronze Frame",
                "A bronze border around your avatar.", Price: 500),
            [Cosmetic.SilverFrame] = new(Cosmetic.SilverFrame, CosmeticKind.AvatarFrame, "Silver Frame",
                "A polished silver border around your avatar.", Price: 1000),
            [Cosmetic.GoldFrame] = new(Cosmetic.GoldFrame, CosmeticKind.AvatarFrame, "Gold Frame",
                "A glowing gold border around your avatar.", Price: 2000),
            [Cosmetic.DragonfireFrame] = new(Cosmetic.DragonfireFrame, CosmeticKind.AvatarFrame, "Dragonfire Frame",
                "A blazing border that tells everyone you've made it.", Price: 4000),
            [Cosmetic.EmberCards] = new(Cosmetic.EmberCards, CosmeticKind.CardSkin, "Ember Card Skin",
                "Your battle cards smoulder in red and orange.", Price: 750),
            [Cosmetic.FrostCards] = new(Cosmetic.FrostCards, CosmeticKind.CardSkin, "Frost Card Skin",
                "Your battle cards turn to ice.", Price: 1500),
            [Cosmetic.VoidCards] = new(Cosmetic.VoidCards, CosmeticKind.CardSkin, "Void Card Skin",
                "Your battle cards swirl with the dark between the stars.", Price: 3000)
        };

    public static IEnumerable<CosmeticDefinition> All => Definitions.Values;

    public static CosmeticDefinition Get(Cosmetic cosmetic) =>
        Definitions.TryGetValue(cosmetic, out var definition)
            ? definition
            : throw new ArgumentOutOfRangeException(nameof(cosmetic), cosmetic, "Unknown cosmetic.");
}

/// <summary>A cosmetic the player owns.</summary>
public class OwnedCosmetic
{
    public Cosmetic Cosmetic { get; private set; }

    private OwnedCosmetic() { }

    internal OwnedCosmetic(Cosmetic cosmetic) => Cosmetic = cosmetic;
}
