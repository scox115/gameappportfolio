using Game.Core.Battles;
using Game.Core.Entities;
using Game.Core.Services;

namespace Game.Core.Tests.Battles;

public class HeroicBossTests
{
    private static readonly DateTime Now = new(2026, 10, 6, 12, 0, 0, DateTimeKind.Utc);
    private readonly MatchRulesEngine _engine = new(new FixedClock(FixedClock.QuietDay));

    private sealed class LowRolls : IBattleRandom
    {
        public int Next(int minInclusive, int maxExclusive) => minInclusive;
    }

    private static Player MaxedPlayer()
    {
        var player = new Player("hero", Player.StartingGold);
        foreach (var card in BattleCards.All)
        {
            while (player.CardLevel(card.Card) < BattleCards.MaxLevel) player.UpgradeCard(card.Card);
        }
        return player;
    }

    [Fact]
    public void HeroicBoss_IsTougherThanTheNormalBoss()
    {
        var heroic = BossProfile.Heroic;
        var normal = BossProfile.Normal;

        Assert.True(heroic.MaxHp > normal.MaxHp);
        Assert.True(heroic.OpeningAttack > normal.OpeningAttack);
        Assert.True(heroic.AttackBase > normal.AttackBase);
        Assert.True(heroic.EnrageBelowPercent > normal.EnrageBelowPercent);
        Assert.True(heroic.CrushingBlowChance > normal.CrushingBlowChance);
    }

    [Fact]
    public void Start_Heroic_UsesTheHeroicBoss()
    {
        var battle = PveBattle.Start(Guid.NewGuid(), Now, new BattleLoadout(3, 3, 3, 0), BossDifficulty.Heroic);

        Assert.Equal(BossDifficulty.Heroic, battle.Difficulty);
        Assert.Equal(BossProfile.Heroic.MaxHp, battle.BossHp);
        Assert.Equal(BossProfile.Heroic.OpeningAttack, battle.BossNextAttack);
    }

    [Fact]
    public void Start_DefaultsToTheNormalBoss()
    {
        var battle = PveBattle.Start(Guid.NewGuid(), Now);

        Assert.Equal(BossDifficulty.Normal, battle.Difficulty);
        Assert.Equal(PveBattle.BossMaxHp, battle.BossHp);
    }

    [Fact]
    public void HeroicBoss_EnragesAtHalfHealth()
    {
        // Level-3 Dragon Claw hits for 45: 200 -> 155 -> 110 (not yet enraged), Fireball 30 -> 80.
        var battle = PveBattle.Start(Guid.NewGuid(), Now, new BattleLoadout(3, 3, 3, 0), BossDifficulty.Heroic);
        battle.PlayCard(BattleCard.DragonClaw, new LowRolls(), Now);
        battle.PlayCard(BattleCard.Fireball, new LowRolls(), Now);
        Assert.False(battle.IsEnraged);

        battle.PlayCard(BattleCard.DragonClaw, new LowRolls(), Now);

        Assert.Equal(BossProfile.Heroic.MaxHp - 120, battle.BossHp);
        Assert.True(battle.IsEnraged);
        var normalHit = BossProfile.Heroic.AttackBase + 4 * BossProfile.Heroic.AttackGrowthPerTurn;
        Assert.Equal(normalHit * BossProfile.Heroic.EnrageDamagePercent / 100, battle.BossNextAttack);
    }

    [Fact]
    public void CanFightHeroicBoss_OnlyWithEveryCardMaxedOut()
    {
        var player = new Player("hero", Player.StartingGold);
        Assert.False(player.CanFightHeroicBoss);

        player.UpgradeCard(BattleCard.Fireball);
        player.UpgradeCard(BattleCard.Fireball);
        player.UpgradeCard(BattleCard.DragonClaw);
        player.UpgradeCard(BattleCard.DragonClaw);
        player.UpgradeCard(BattleCard.HolyShield);
        Assert.False(player.CanFightHeroicBoss);

        player.UpgradeCard(BattleCard.HolyShield);
        Assert.True(player.CanFightHeroicBoss);
    }

    [Fact]
    public void HeroicWin_PaysTheHeroicRewardOnceADay()
    {
        var player = MaxedPlayer();

        var first = _engine.ProcessPveMatch(GameMatch.CreatePve(player.Id), player, isVictory: true, BossDifficulty.Heroic);
        var second = _engine.ProcessPveMatch(GameMatch.CreatePve(player.Id), player, isVictory: true, BossDifficulty.Heroic);

        Assert.False(first.ReducedBossReward);
        Assert.Equal(MatchRulesEngine.HeroicWinExperience, first.Experience);
        Assert.Equal(MatchRulesEngine.HeroicWinGold, first.Gold - first.Bonuses.Gold);
        Assert.True(second.ReducedBossReward);
        Assert.Equal(MatchRulesEngine.ReducedBossWinGold, second.Gold - second.Bonuses.Gold);
        Assert.Equal(MatchRulesEngine.WinExperience, second.Experience);
    }

    [Fact]
    public void HeroicWin_DoesntUseUpTheNormalBossWins()
    {
        var player = MaxedPlayer();
        var today = FixedClock.QuietDay;

        _engine.ProcessPveMatch(GameMatch.CreatePve(player.Id), player, isVictory: true, BossDifficulty.Heroic);

        Assert.Equal(Player.FullRewardBossWinsPerDay, player.FullRewardBossWinsLeft(today));
        Assert.False(player.HeroicRewardAvailable(today));
        Assert.True(player.HeroicRewardAvailable(today.AddDays(1)));
    }

    [Fact]
    public void HeroicLoss_PaysTheNormalConsolationAndKeepsTheHeroicReward()
    {
        var player = MaxedPlayer();

        var reward = _engine.ProcessPveMatch(GameMatch.CreatePve(player.Id), player, isVictory: false, BossDifficulty.Heroic);

        Assert.Equal(MatchRulesEngine.LossGold, reward.Gold - reward.Bonuses.Gold);
        Assert.Equal(MatchRulesEngine.LossExperience, reward.Experience);
        Assert.True(player.HeroicRewardAvailable(FixedClock.QuietDay));
    }
}
