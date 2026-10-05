using Game.Core.Battles;
using Game.Core.Entities;
using Game.Core.Shop;

namespace Game.Core.Tests.Shop;

public class GoldShopTests
{
    private static Player PlayerWith(int gold) => new("Shopper", gold);

    [Fact]
    public void BuyingAnUpgrade_TakesTheGoldAndRaisesTheCardLevel()
    {
        var player = PlayerWith(500);

        var offer = GoldShop.Buy(player, ShopItem.FireballUpgrade);

        Assert.Equal(350, player.Gold);
        Assert.Equal(2, player.CardLevel(BattleCard.Fireball));
        Assert.Equal(2, offer.Owned);
        Assert.Equal(300, offer.Price);
    }

    [Fact]
    public void ACardStopsAtItsTopLevel()
    {
        var player = PlayerWith(1000);
        GoldShop.Buy(player, ShopItem.DragonClawUpgrade);
        GoldShop.Buy(player, ShopItem.DragonClawUpgrade);

        Assert.Null(GoldShop.OfferFor(player, ShopItem.DragonClawUpgrade).Price);
        var error = Assert.Throws<InvalidOperationException>(() => GoldShop.Buy(player, ShopItem.DragonClawUpgrade));
        Assert.Contains("top level", error.Message);
        Assert.Equal(550, player.Gold);
    }

    [Fact]
    public void YouCantBuyWhatYouCantAfford()
    {
        var player = PlayerWith(100);

        var error = Assert.Throws<InvalidOperationException>(() => GoldShop.Buy(player, ShopItem.HolyShieldUpgrade));

        Assert.Contains("150 gold", error.Message);
        Assert.Equal(100, player.Gold);
        Assert.Equal(1, player.CardLevel(BattleCard.HolyShield));
    }

    [Fact]
    public void ElixirsStackUpToTheLimit()
    {
        var player = PlayerWith(1000);
        for (var i = 0; i < Player.MaxElixirs; i++) GoldShop.Buy(player, ShopItem.BattleElixir);

        Assert.Equal(Player.MaxElixirs, player.Elixirs);
        Assert.Throws<InvalidOperationException>(() => GoldShop.Buy(player, ShopItem.BattleElixir));
        Assert.Equal(1000 - Player.MaxElixirs * GoldShop.ElixirPrice, player.Gold);
    }

    [Fact]
    public void BuyingSomethingChangesThePlayersConcurrencyVersion()
    {
        var player = PlayerWith(500);
        var before = player.Version;

        GoldShop.Buy(player, ShopItem.BattleElixir);

        Assert.NotEqual(before, player.Version);
    }

    [Fact]
    public void TheLoadoutCarriesUpgradesAndUsesOneElixir()
    {
        var player = PlayerWith(1000);
        GoldShop.Buy(player, ShopItem.HolyShieldUpgrade);
        GoldShop.Buy(player, ShopItem.BattleElixir);

        var first = player.TakeLoadoutForBossFight();
        var second = player.TakeLoadoutForBossFight();

        Assert.Equal(new BattleLoadout(1, 2, 1, Player.ElixirBonusHp), first);
        Assert.Equal(new BattleLoadout(1, 2, 1, 0), second);
        Assert.Equal(0, player.Elixirs);
    }

    [Fact]
    public void UpgradesAddDamageOrHealingButKeepTheRisk()
    {
        var fireball = BattleCards.AtLevel(BattleCard.Fireball, 3);
        var shield = BattleCards.AtLevel(BattleCard.HolyShield, 2);

        Assert.Equal(30, fireball.Damage);
        Assert.Equal(0, fireball.Heal);
        Assert.Equal(BattleCards.Get(BattleCard.Fireball).FailChance, fireball.FailChance);
        Assert.Equal(20, shield.Heal);
        Assert.Equal(0, shield.Damage);
    }
}

public class GoldShopPvpTests
{
    private static Player PlayerWith(int gold, int pvpWins = 0)
    {
        var player = new Player("Duelist", gold);
        for (var i = 0; i < pvpWins; i++) player.RecordPvpWin(ratingGained: 0);
        return player;
    }

    [Fact]
    public void ATitleStaysLockedUntilThePlayerHasWonEnoughDuels()
    {
        var player = PlayerWith(1000, pvpWins: 4);

        var offer = GoldShop.OfferFor(player, ShopItem.TitleGladiator);
        var error = Assert.Throws<InvalidOperationException>(() => GoldShop.Buy(player, ShopItem.TitleGladiator));

        Assert.Equal("Win 1 more duel to unlock", offer.LockedReason);
        Assert.Contains("1 more duel", error.Message);
        Assert.Equal(1000, player.Gold);
        Assert.False(player.OwnsTitle(PlayerTitle.Gladiator));
    }

    [Fact]
    public void BuyingATitleShowsItAfterTheName()
    {
        var player = PlayerWith(1000, pvpWins: 5);

        var offer = GoldShop.Buy(player, ShopItem.TitleGladiator);

        Assert.Equal(700, player.Gold);
        Assert.Equal(PlayerTitle.Gladiator, player.EquippedTitle);
        Assert.Equal("the Gladiator", player.TitleName);
        Assert.Null(offer.Price);
        Assert.Throws<InvalidOperationException>(() => GoldShop.Buy(player, ShopItem.TitleGladiator));
    }

    [Fact]
    public void APlayerCanOnlyShowATitleTheyOwn()
    {
        var player = PlayerWith(1000, pvpWins: 1);
        GoldShop.Buy(player, ShopItem.TitleDuelist);

        player.EquipTitle(null);
        Assert.Null(player.TitleName);

        Assert.Throws<InvalidOperationException>(() => player.EquipTitle(PlayerTitle.ArenaChampion));
        player.EquipTitle(PlayerTitle.Duelist);
        Assert.Equal("the Duelist", player.TitleName);
    }

    [Fact]
    public void ADuelElixirIsUsedByTheNextDuelNotTheNextBossFight()
    {
        var player = PlayerWith(1000);
        GoldShop.Buy(player, ShopItem.DuelElixir);
        GoldShop.Buy(player, ShopItem.FireballUpgrade);

        var bossFight = player.TakeLoadoutForBossFight();
        var duel = player.TakeLoadoutForDuel();
        var nextDuel = player.TakeLoadoutForDuel();

        Assert.Equal(0, bossFight.BonusHp);
        Assert.Equal(new BattleLoadout(2, 1, 1, Player.DuelElixirBonusHp), duel);
        Assert.Equal(0, nextDuel.BonusHp);
        Assert.Equal(1000 - GoldShop.DuelElixirPrice - 150, player.Gold);
    }
}
