using Game.Core.Entities;
using Game.Core.Shop;

namespace Game.Core.Tests.Shop;

public class CosmeticsTests
{
    [Fact]
    public void BuyingAFrame_TakesTheGoldAndWearsIt()
    {
        var player = new Player("Peacock", 600);

        var offer = GoldShop.Buy(player, ShopItem.FrameBronze);

        Assert.Equal(100, player.Gold);
        Assert.Equal(Cosmetic.BronzeFrame, player.EquippedFrame);
        Assert.Null(player.EquippedCardSkin);
        Assert.Null(offer.Price);
        Assert.Equal(1, offer.Owned);
    }

    [Fact]
    public void ACosmeticCanOnlyBeBoughtOnce()
    {
        var player = new Player("Peacock", 2000);
        GoldShop.Buy(player, ShopItem.CardSkinEmber);

        var error = Assert.Throws<InvalidOperationException>(() => GoldShop.Buy(player, ShopItem.CardSkinEmber));

        Assert.Contains("already own", error.Message);
        Assert.Equal(1250, player.Gold);
    }

    [Fact]
    public void EveryPrestigeItemTogether_IsABigLongTermGoal()
    {
        Assert.Equal(12_750, CosmeticCatalog.All.Sum(c => c.Price));
    }

    [Fact]
    public void OwnedCosmeticsCanBeSwappedOrTakenOff()
    {
        var player = new Player("Peacock", 5000);
        GoldShop.Buy(player, ShopItem.FrameBronze);
        GoldShop.Buy(player, ShopItem.FrameSilver);

        player.EquipCosmetic(CosmeticKind.AvatarFrame, Cosmetic.BronzeFrame);
        Assert.Equal(Cosmetic.BronzeFrame, player.EquippedFrame);

        player.EquipCosmetic(CosmeticKind.AvatarFrame, null);
        Assert.Null(player.EquippedFrame);
    }

    [Fact]
    public void ACosmeticMustBeOwnedAndWornInItsOwnSlot()
    {
        var player = new Player("Peacock", 5000);
        GoldShop.Buy(player, ShopItem.CardSkinFrost);

        Assert.Throws<InvalidOperationException>(() => player.EquipCosmetic(CosmeticKind.AvatarFrame, Cosmetic.GoldFrame));
        Assert.Throws<InvalidOperationException>(() => player.EquipCosmetic(CosmeticKind.AvatarFrame, Cosmetic.FrostCards));
    }
}
