using Game.Core.Entities;
using Game.Core.Shop;

namespace Game.Api.Models;

public record PurchaseRequest(ShopItem Item);

/// <param name="Price">Null when the player can't buy any more of this item.</param>
/// <param name="LockedReason">Why it can't be bought yet, such as a title that needs more duel wins.</param>
public record ShopOfferResponse(ShopItem Item, string Name, string Description, int? Price, int Owned, int MaxOwned, string? LockedReason)
{
    public static ShopOfferResponse From(ShopOffer offer) =>
        new(offer.Item, offer.Name, offer.Description, offer.Price, offer.Owned, offer.MaxOwned, offer.LockedReason);
}

/// <param name="EquippedTitle">The title shown after the player's name, or null.</param>
/// <param name="EquippedFrame">The avatar frame the player wears, or null.</param>
/// <param name="EquippedCardSkin">The card skin the player uses, or null.</param>
public record ShopResponse(
    int Gold, int PvpWins, PlayerTitle? EquippedTitle, Cosmetic? EquippedFrame, Cosmetic? EquippedCardSkin, IReadOnlyList<ShopOfferResponse> Offers)
{
    public static ShopResponse For(Player player) =>
        new(player.Gold, player.PvpWins, player.EquippedTitle, player.EquippedFrame, player.EquippedCardSkin,
            GoldShop.OffersFor(player).Select(ShopOfferResponse.From).ToList());
}

/// <param name="Cosmetic">The owned cosmetic to wear, or null to wear none of this kind.</param>
public record EquipCosmeticRequest(CosmeticKind Kind, Cosmetic? Cosmetic);

/// <param name="Title">The owned title to show, or null to show none.</param>
public record EquipTitleRequest(PlayerTitle? Title);
