using Game.Core.Battles;
using Game.Core.Entities;

namespace Game.Core.Shop;

public enum ShopItem
{
    FireballUpgrade,
    HolyShieldUpgrade,
    DragonClawUpgrade,
    BattleElixir
}

/// <summary>What a shop item is and what it costs this player right now.</summary>
/// <param name="Price">Null when the player can't buy any more (a card at its top level, or a full elixir pouch).</param>
public record ShopOffer(ShopItem Item, string Name, string Description, int? Price, int Owned, int MaxOwned);

/// <summary>
/// Where players spend the gold they win. Card upgrades make a card stronger against the boss
/// (PvP duels stay even), and a Battle Elixir adds health for the next boss fight.
/// </summary>
public static class GoldShop
{
    public const int ElixirPrice = 50;

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

        if (item == ShopItem.BattleElixir)
        {
            return new ShopOffer(item, "Battle Elixir",
                $"Start your next boss fight with +{Player.ElixirBonusHp} HP. Used automatically.",
                player.Elixirs < Player.MaxElixirs ? ElixirPrice : null, player.Elixirs, Player.MaxElixirs);
        }

        var card = CardFor(item);
        var definition = BattleCards.Get(card);
        var level = player.CardLevel(card);
        var effect = definition.Damage > 0 ? "damage" : "healing";
        int? price = level < BattleCards.MaxLevel ? UpgradePrices[level - 1] : null;

        return new ShopOffer(item, $"{definition.Name} Upgrade",
            $"+{BattleCards.BonusPerLevel} {effect} against the Shadow Overlord for each level.",
            price, level, BattleCards.MaxLevel);
    }

    /// <summary>Takes the gold and hands over the item. Throws when the player can't afford it or already has the most allowed.</summary>
    public static ShopOffer Buy(Player player, ShopItem item)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (!Enum.IsDefined(item)) throw new ArgumentOutOfRangeException(nameof(item), item, "Unknown shop item.");

        var offer = OfferFor(player, item);
        if (offer.Price is not { } price)
        {
            throw new InvalidOperationException(item == ShopItem.BattleElixir
                ? $"You can carry at most {Player.MaxElixirs} elixirs."
                : $"{offer.Name.Replace(" Upgrade", "")} is already at its top level.");
        }

        if (player.Gold < price)
        {
            throw new InvalidOperationException($"You need {price} gold for this, and you have {player.Gold}.");
        }

        player.DeductGold(price);
        if (item == ShopItem.BattleElixir)
        {
            player.AddElixir();
        }
        else
        {
            player.UpgradeCard(CardFor(item));
        }

        return OfferFor(player, item);
    }

    private static BattleCard CardFor(ShopItem item) => item switch
    {
        ShopItem.FireballUpgrade => BattleCard.Fireball,
        ShopItem.HolyShieldUpgrade => BattleCard.HolyShield,
        ShopItem.DragonClawUpgrade => BattleCard.DragonClaw,
        _ => throw new ArgumentOutOfRangeException(nameof(item), item, "Not a card upgrade.")
    };
}
