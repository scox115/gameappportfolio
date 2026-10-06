using Game.Core.Battles;
using Game.Core.Entities;

namespace Game.Core.Tests.Battles;

public class HeroClassTests
{
    private static readonly DateTime Now = new(2026, 10, 6, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(HeroClass.Sorcerer, BattleCard.Fireball)]
    [InlineData(HeroClass.Paladin, BattleCard.HolyShield)]
    [InlineData(HeroClass.Ranger, BattleCard.DragonClaw)]
    public void EachClass_BoostsOnlyItsSignatureCard(HeroClass heroClass, BattleCard signature)
    {
        Assert.Equal(signature, HeroClasses.Get(heroClass).SignatureCard);

        foreach (var card in BattleCards.All.Select(c => c.Card))
        {
            for (var level = 1; level <= BattleCards.MaxLevel; level++)
            {
                var plain = BattleCards.AtLevel(card, level);
                var held = HeroClasses.CardFor(heroClass, card, level);
                if (card == signature) Assert.NotEqual(plain, held);
                else Assert.Equal(plain, held);
            }
        }
    }

    [Fact]
    public void Sorcerer_FireballHitsHarderAndIsResistedLess()
    {
        var fireball = HeroClasses.CardFor(HeroClass.Sorcerer, BattleCard.Fireball, 1);

        Assert.Equal(22, fireball.Damage);
        Assert.Equal(5, fireball.FailChance);
    }

    [Fact]
    public void Paladin_HolyShieldHealsMoreAndIsInterruptedLess()
    {
        var shield = HeroClasses.CardFor(HeroClass.Paladin, BattleCard.HolyShield, 3);

        Assert.Equal(30, shield.Heal);
        Assert.Equal(15, shield.FailChance);
        Assert.Equal(0, shield.Damage);
    }

    [Fact]
    public void Ranger_DragonClawIsDodgedLess()
    {
        var claw = HeroClasses.CardFor(HeroClass.Ranger, BattleCard.DragonClaw, 1);

        Assert.Equal(35, claw.Damage);
        Assert.Equal(20, claw.FailChance);
    }

    [Fact]
    public void ABossFight_UsesTheHerosClass()
    {
        var battle = PveBattle.Start(Guid.NewGuid(), Now, new BattleLoadout(1, 1, 1, 0, HeroClass.Ranger));

        Assert.Equal(HeroClass.Ranger, battle.Class);
        Assert.Equal(20, battle.CardFor(BattleCard.DragonClaw).FailChance);
        Assert.Equal(PveBattle.BasePlayerMaxHp + HeroClasses.Get(HeroClass.Ranger).BonusHp, battle.PlayerMaxHp);
    }

    [Fact]
    public void ADuel_GivesEachPlayerTheirOwnClass()
    {
        Guid alice = Guid.NewGuid(), bob = Guid.NewGuid();
        var battle = PvpBattle.Start(alice, bob, Now,
            new BattleLoadout(1, 1, 1, 0, HeroClass.Sorcerer), new BattleLoadout(1, 1, 1, 0, HeroClass.Paladin));

        Assert.Equal(HeroClass.Sorcerer, battle.ClassOf(alice));
        Assert.Equal(HeroClass.Paladin, battle.ClassOf(bob));
        Assert.Equal(22, battle.CardFor(alice, BattleCard.Fireball).Damage);
        Assert.Equal(20, battle.CardFor(bob, BattleCard.Fireball).Damage);
        Assert.Equal(20, battle.CardFor(bob, BattleCard.HolyShield).Heal);
    }

    [Fact]
    public void NewHeroes_AreSorcerersAndCanPickAnotherClassForFree()
    {
        var player = new Player("hero", Player.StartingGold);
        Assert.Equal(HeroClass.Sorcerer, player.Class);

        player.ChooseStartingClass(HeroClass.Paladin);

        Assert.Equal(HeroClass.Paladin, player.Class);
        Assert.Equal(Player.StartingGold, player.Gold);
        Assert.Equal(HeroClass.Paladin, player.TakeLoadoutForBossFight().Class);
        Assert.Equal(HeroClass.Paladin, player.TakeLoadoutForDuel().Class);
    }

    [Fact]
    public void ChangingClassLater_CostsGold()
    {
        var player = new Player("hero", HeroClasses.ChangePrice + 50);

        player.ChangeClass(HeroClass.Ranger);

        Assert.Equal(HeroClass.Ranger, player.Class);
        Assert.Equal(50, player.Gold);
    }

    [Fact]
    public void ChangingClass_NeedsEnoughGoldAndADifferentClass()
    {
        var player = new Player("hero", HeroClasses.ChangePrice - 1);

        Assert.Throws<InvalidOperationException>(() => player.ChangeClass(HeroClass.Sorcerer));
        Assert.Throws<InvalidOperationException>(() => player.ChangeClass(HeroClass.Ranger));
        Assert.Equal(HeroClass.Sorcerer, player.Class);
        Assert.Equal(HeroClasses.ChangePrice - 1, player.Gold);
    }

    [Fact]
    public void UnknownClasses_AreRejected()
    {
        var player = new Player("hero", 1000);

        Assert.Throws<ArgumentOutOfRangeException>(() => player.ChooseStartingClass((HeroClass)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => player.ChangeClass((HeroClass)99));
    }
}
