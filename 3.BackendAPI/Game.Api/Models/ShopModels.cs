using Game.Core.Entities;
using Game.Core.Shop;

namespace Game.Api.Models;

public record PurchaseRequest(ShopItem Item);

/// <param name="Price">Null when the player can't buy any more of this item.</param>
public record ShopOfferResponse(ShopItem Item, string Name, string Description, int? Price, int Owned, int MaxOwned)
{
    public static ShopOfferResponse From(ShopOffer offer) =>
        new(offer.Item, offer.Name, offer.Description, offer.Price, offer.Owned, offer.MaxOwned);
}

public record ShopResponse(int Gold, IReadOnlyList<ShopOfferResponse> Offers)
{
    public static ShopResponse For(Player player) =>
        new(player.Gold, GoldShop.OffersFor(player).Select(ShopOfferResponse.From).ToList());
}
