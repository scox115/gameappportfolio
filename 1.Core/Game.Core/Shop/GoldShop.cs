using Game.Core.Battles;
using Game.Core.Entities;

namespace Game.Core.Shop;

public enum ShopItem
{
    FireballUpgrade,
    HolyShieldUpgrade,
    DragonClawUpgrade,
    BattleElixir,
    DuelElixir,
    TitleDuelist,
    TitleGladiator,
    TitleArenaChampion,
    FrameBronze,
    FrameSilver,
    FrameGold,
    FrameDragonfire,
    CardSkinEmber,
    CardSkinFrost,
    CardSkinVoid
}

/// <summary>What a shop item is and what it costs this player right now.</summary>
/// <param name="Price">Null when the player can't buy any more (a card at its top level, a full elixir pouch, or a title they own).</param>
/// <param name="LockedReason">Why the player can't buy it yet, such as a title that needs more duel wins.</param>
public record ShopOffer(ShopItem Item, string Name, string Description, int? Price, int Owned, int MaxOwned, string? LockedReason = null);

/// <summary>
/// Where players spend the gold they win. Card upgrades make a card stronger in boss fights and
/// duels, elixirs add health for the next boss fight or duel, titles show off duel wins, and
/// prestige cosmetics give long-time players something big to save for without changing balance.
/// </summary>
public static class GoldShop
{
    public const int ElixirPrice = 50;
    public const int DuelElixirPrice = 60;

    /// <summary>Upgrade prices by the level being bought: level 2, then level 3.</summary>
    private static readonly int[] UpgradePrices = [150, 300];

    public static IReadOnlyList<ShopOffer> OffersFor(Player player)
    {
        ArgumentNullException.ThrowIfNull(player);
        return Enum.GetValues<ShopItem>().Select(item => OfferFor(player, item)).ToList();
    }

    public static ShopOffer OfferFor(Player player, ShopItem item)
    {
        ArgumentNullException.ThrowIfNull(player);

        if (TitleFor(item) is { } title)
        {
            var titleDefinition = PlayerTitles.Get(title);
            var owned = player.OwnsTitle(title);
            return new ShopOffer(item, $"Title: {titleDefinition.Name}",
                $"Shown after your name to duel opponents and on the leaderboard. Needs {titleDefinition.DuelWinsNeeded} PvP win{(titleDefinition.DuelWinsNeeded == 1 ? "" : "s")}.",
                owned ? null : titleDefinition.Price, owned ? 1 : 0, 1,
                owned || player.PvpWins >= titleDefinition.DuelWinsNeeded ? null : $"Win {titleDefinition.DuelWinsNeeded - player.PvpWins} more duel{(titleDefinition.DuelWinsNeeded - player.PvpWins == 1 ? "" : "s")} to unlock");
        }

        if (CosmeticFor(item) is { } cosmetic)
        {
            var cosmeticDefinition = CosmeticCatalog.Get(cosmetic);
            var owned = player.OwnsCosmetic(cosmetic);
            return new ShopOffer(item, cosmeticDefinition.Name, $"{cosmeticDefinition.Description} Looks only; it never changes a battle.",
                owned ? null : cosmeticDefinition.Price, owned ? 1 : 0, 1);
        }

        if (item == ShopItem.BattleElixir)
        {
            return new ShopOffer(item, "Battle Elixir",
                $"Start your next boss fight with +{Player.ElixirBonusHp} HP. Used automatically.",
                player.Elixirs < Player.MaxElixirs ? ElixirPrice : null, player.Elixirs, Player.MaxElixirs);
        }

        if (item == ShopItem.DuelElixir)
        {
            return new ShopOffer(item, "Duel Elixir",
                $"Start your next PvP duel with +{Player.DuelElixirBonusHp} HP. Used automatically.",
                player.DuelElixirs < Player.MaxElixirs ? DuelElixirPrice : null, player.DuelElixirs, Player.MaxElixirs);
        }

        var card = CardFor(item);
        var definition = BattleCards.Get(card);
        var level = player.CardLevel(card);
        var effect = definition.Damage > 0 ? "damage" : "healing";
        int? price = level < BattleCards.MaxLevel ? UpgradePrices[level - 1] : null;

        return new ShopOffer(item, $"{definition.Name} Upgrade",
            $"+{BattleCards.BonusPerLevel} {effect} per level, in boss fights and PvP duels.",
            price, level, BattleCards.MaxLevel);
    }

    /// <summary>Takes the gold and hands over the item. Throws when the player can't afford it or already has the most allowed.</summary>
    public static ShopOffer Buy(Player player, ShopItem item)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (!Enum.IsDefined(item)) throw new ArgumentOutOfRangeException(nameof(item), item, "Unknown shop item.");

        var offer = OfferFor(player, item);
        if (offer.LockedReason is not null)
        {
            throw new InvalidOperationException($"{offer.LockedReason}.");
        }

        if (offer.Price is not { } price)
        {
            if (TitleFor(item) is not null) throw new InvalidOperationException("You already own that title.");
            if (CosmeticFor(item) is not null) throw new InvalidOperationException($"You already own the {offer.Name}.");
            throw new InvalidOperationException(item is ShopItem.BattleElixir or ShopItem.DuelElixir
                ? $"You can carry at most {Player.MaxElixirs} of those elixirs."
                : $"{offer.Name.Replace(" Upgrade", "")} is already at its top level.");
        }

        if (player.Gold < price)
        {
            throw new InvalidOperationException($"You need {price} gold for this, and you have {player.Gold}.");
        }

        player.DeductGold(price);
        if (TitleFor(item) is { } boughtTitle)
        {
            player.AddTitle(boughtTitle);
        }
        else if (CosmeticFor(item) is { } boughtCosmetic)
        {
            player.AddCosmetic(boughtCosmetic);
        }
        else if (item == ShopItem.BattleElixir)
        {
            player.AddElixir();
        }
        else if (item == ShopItem.DuelElixir)
        {
            player.AddDuelElixir();
        }
        else
        {
            player.UpgradeCard(CardFor(item));
        }

        return OfferFor(player, item);
    }

    /// <summary>The title a shop item sells, or null for other items.</summary>
    public static PlayerTitle? TitleFor(ShopItem item) => item switch
    {
        ShopItem.TitleDuelist => PlayerTitle.Duelist,
        ShopItem.TitleGladiator => PlayerTitle.Gladiator,
        ShopItem.TitleArenaChampion => PlayerTitle.ArenaChampion,
        _ => null
    };

    /// <summary>The cosmetic a shop item sells, or null for other items.</summary>
    public static Cosmetic? CosmeticFor(ShopItem item) => item switch
    {
        ShopItem.FrameBronze => Cosmetic.BronzeFrame,
        ShopItem.FrameSilver => Cosmetic.SilverFrame,
        ShopItem.FrameGold => Cosmetic.GoldFrame,
        ShopItem.FrameDragonfire => Cosmetic.DragonfireFrame,
        ShopItem.CardSkinEmber => Cosmetic.EmberCards,
        ShopItem.CardSkinFrost => Cosmetic.FrostCards,
        ShopItem.CardSkinVoid => Cosmetic.VoidCards,
        _ => null
    };

    private static BattleCard CardFor(ShopItem item) => item switch
    {
        ShopItem.FireballUpgrade => BattleCard.Fireball,
        ShopItem.HolyShieldUpgrade => BattleCard.HolyShield,
        ShopItem.DragonClawUpgrade => BattleCard.DragonClaw,
        _ => throw new ArgumentOutOfRangeException(nameof(item), item, "Not a card upgrade.")
    };
}
