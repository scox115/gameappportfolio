using Game.Core.Battles;

namespace Game.Core.Tests.Battles;

public class PveBattleTests
{
    private static readonly DateTime Now = new(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc);

    // Each turn rolls, in order: does the card fail (0-99), the boss's next move (0-99),
    // then the extra damage (0-5). Low rolls favour the player.
    private sealed class ScriptedRandom(int fallback, params int[] rolls) : IBattleRandom
    {
        private readonly Queue<int> _rolls = new(rolls);

        public int Next(int minInclusive, int maxExclusive) =>
            Math.Clamp(_rolls.TryDequeue(out var roll) ? roll : fallback, minInclusive, maxExclusive - 1);
    }

    private static IBattleRandom Lucky(params int[] rolls) => new ScriptedRandom(0, rolls);

    // A Paladin's class boost is on Holy Shield, so Fireball and Dragon Claw keep their base numbers.
    private static readonly BattleLoadout Plain = new(1, 1, 1, 0, HeroClass.Paladin);
    private static IBattleRandom Unlucky => new ScriptedRandom(99);

    [Fact]
    public void Start_SetsFullHealthAndTheOpeningSlash()
    {
        var playerId = Guid.NewGuid();
        var battle = PveBattle.Start(playerId, Now);

        Assert.Equal(playerId, battle.PlayerId);
        Assert.Equal(PveBattle.BasePlayerMaxHp, battle.PlayerHp);
        Assert.Equal(PveBattle.BossMaxHp, battle.BossHp);
        Assert.Equal(BossMove.Slash, battle.BossNextMove);
        Assert.Equal(PveBattle.OpeningBossAttack, battle.BossNextAttack);
        Assert.Null(battle.RechargingCard);
        Assert.Equal(1, battle.Turn);
        Assert.Equal(BattleStatus.InProgress, battle.Status);
    }

    [Fact]
    public void Start_RejectsEmptyPlayerId()
    {
        Assert.Throws<ArgumentException>(() => PveBattle.Start(Guid.Empty, Now));
    }

    [Fact]
    public void PlayCard_DamagesBossThenBossStrikesAndAnnouncesItsNextMove()
    {
        var battle = PveBattle.Start(Guid.NewGuid(), Now, Plain);

        var result = battle.PlayCard(BattleCard.Fireball, Lucky(), Now);

        Assert.False(result.CardFailed);
        Assert.Equal(20, result.DamageDealt);
        Assert.Equal(BossMove.Slash, result.BossMove);
        Assert.Equal(15, result.BossDamage);
        Assert.Equal(PveBattle.BossMaxHp - 20, battle.BossHp);
        Assert.Equal(85, battle.PlayerHp);
        Assert.Equal(2, battle.Turn);
        Assert.Equal(BossMove.Slash, battle.BossNextMove);
        Assert.Equal(PveBattle.BossAttackBase + (2 * 2), battle.BossNextAttack);
    }

    [Theory]
    [InlineData(BattleCard.Fireball, 90)]
    [InlineData(BattleCard.DragonClaw, 75)]
    public void PlayCard_TheBossCanResistOrDodgeAttacks(BattleCard card, int failingRoll)
    {
        var battle = PveBattle.Start(Guid.NewGuid(), Now, Plain);

        var result = battle.PlayCard(card, Lucky(failingRoll), Now);

        Assert.True(result.CardFailed);
        Assert.Equal(0, result.DamageDealt);
        Assert.Equal(PveBattle.BossMaxHp, battle.BossHp);
        Assert.Equal(15, result.BossDamage);
    }

    [Fact]
    public void PlayCard_JustBelowTheFailChanceStillLands()
    {
        var battle = PveBattle.Start(Guid.NewGuid(), Now);

        var result = battle.PlayCard(BattleCard.DragonClaw, Lucky(74), Now);

        Assert.False(result.CardFailed);
        Assert.Equal(35, result.DamageDealt);
    }

    [Fact]
    public void HolyShield_BlocksTheBossAttack()
    {
        var battle = PveBattle.Start(Guid.NewGuid(), Now);

        var result = battle.PlayCard(BattleCard.HolyShield, Lucky(), Now);

        Assert.True(result.AttackBlocked);
        Assert.Equal(0, result.BossDamage);
        Assert.Equal(PveBattle.BasePlayerMaxHp, battle.PlayerHp);
    }

    [Fact]
    public void HolyShield_CanBeInterrupted()
    {
        var battle = PveBattle.Start(Guid.NewGuid(), Now);
        battle.PlayCard(BattleCard.Fireball, Lucky(), Now);

        var result = battle.PlayCard(BattleCard.HolyShield, Lucky(80), Now);

        Assert.True(result.CardFailed);
        Assert.False(result.AttackBlocked);
        Assert.Equal(0, result.HealthRestored);
        // The lucky move roll after the first turn is a plain Slash: base + 2 per turn.
        Assert.Equal(PveBattle.BossAttackBase + 4, result.BossDamage);
        Assert.Equal(85 - (PveBattle.BossAttackBase + 4), battle.PlayerHp);
    }

    [Fact]
    public void HolyShield_HealingIsCappedAtMaxHealth()
    {
        var battle = PveBattle.Start(Guid.NewGuid(), Now);
        battle.PlayCard(BattleCard.Fireball, Lucky(), Now);

        var result = battle.PlayCard(BattleCard.HolyShield, Lucky(), Now);

        Assert.Equal(15, result.HealthRestored);
        Assert.Equal(PveBattle.BasePlayerMaxHp, battle.PlayerHp);
    }

    [Theory]
    [InlineData(54, 5, BossMove.Slash, PveBattle.BossAttackBase + 4 + 5)]
    [InlineData(55, 5, BossMove.CrushingBlow, (PveBattle.BossAttackBase + 4 + 5) * 8 / 5)]
    [InlineData(79, 0, BossMove.CrushingBlow, (PveBattle.BossAttackBase + 4) * 8 / 5)]
    [InlineData(80, 0, BossMove.LifeDrain, (PveBattle.BossAttackBase + 4) * 3 / 4)]
    public void PlayCard_TheBossVariesItsMoves(int moveRoll, int extraDamage, BossMove expectedMove, int expectedDamage)
    {
        var battle = PveBattle.Start(Guid.NewGuid(), Now);

        battle.PlayCard(BattleCard.Fireball, Lucky(0, moveRoll, extraDamage), Now);

        Assert.Equal(expectedMove, battle.BossNextMove);
        Assert.Equal(expectedDamage, battle.BossNextAttack);
    }

    [Fact]
    public void LifeDrain_HealsTheBossByTheDamageDealt()
    {
        var battle = PveBattle.Start(Guid.NewGuid(), Now, Plain);
        battle.PlayCard(BattleCard.Fireball, Lucky(0, 80, 0), Now);

        var result = battle.PlayCard(BattleCard.Fireball, Lucky(), Now);

        Assert.Equal(BossMove.LifeDrain, result.BossMove);
        const int drain = (PveBattle.BossAttackBase + 4) * 3 / 4;
        Assert.Equal(drain, result.BossDamage);
        Assert.Equal(drain, result.BossHealed);
        Assert.Equal(PveBattle.BossMaxHp - 20 - 20 + drain, battle.BossHp);
    }

    [Fact]
    public void LifeDrain_HealsNothingWhenBlocked()
    {
        var battle = PveBattle.Start(Guid.NewGuid(), Now, Plain);
        battle.PlayCard(BattleCard.Fireball, Lucky(0, 80, 0), Now);

        var result = battle.PlayCard(BattleCard.HolyShield, Lucky(), Now);

        Assert.True(result.AttackBlocked);
        Assert.Equal(0, result.BossHealed);
        Assert.Equal(PveBattle.BossMaxHp - 20, battle.BossHp);
    }

    [Theory]
    [InlineData(BattleCard.DragonClaw)]
    [InlineData(BattleCard.HolyShield)]
    public void PlayCard_PowerCardsNeedATurnToRecharge(BattleCard card)
    {
        var battle = PveBattle.Start(Guid.NewGuid(), Now);
        battle.PlayCard(card, Lucky(), Now);

        Assert.Equal(card, battle.RechargingCard);
        Assert.False(battle.CanPlay(card));
        Assert.Throws<InvalidOperationException>(() => battle.PlayCard(card, Lucky(), Now));

        battle.PlayCard(BattleCard.Fireball, Lucky(), Now);
        Assert.True(battle.CanPlay(card));
    }

    [Fact]
    public void PlayCard_FireballCanBePlayedEveryTurn()
    {
        var battle = PveBattle.Start(Guid.NewGuid(), Now);
        battle.PlayCard(BattleCard.Fireball, Lucky(), Now);

        Assert.Null(battle.RechargingCard);
        Assert.True(battle.CanPlay(BattleCard.Fireball));
    }

    [Fact]
    public void PlayCard_WinningBlowEndsTheBattleBeforeTheBossAttacks()
    {
        var battle = PveBattle.Start(Guid.NewGuid(), Now);

        var result = PlayUntilFinished(battle, Lucky);

        Assert.Equal(BattleStatus.Won, battle.Status);
        Assert.Null(result.BossMove);
        Assert.Null(result.BossDamage);
        Assert.Equal(0, battle.BossHp);
        Assert.Equal(Now, battle.CompletedAt);
    }

    [Fact]
    public void PlayCard_PlayerAtZeroHealthLoses()
    {
        var battle = PveBattle.Start(Guid.NewGuid(), Now);

        PlayUntilFinished(battle, () => Unlucky);

        Assert.Equal(BattleStatus.Lost, battle.Status);
        Assert.Equal(0, battle.PlayerHp);
    }

    [Fact]
    public void PlayCard_RejectsMovesAfterTheBattleEnds()
    {
        var battle = PveBattle.Start(Guid.NewGuid(), Now);
        PlayUntilFinished(battle, Lucky);

        Assert.Throws<InvalidOperationException>(() => battle.PlayCard(BattleCard.Fireball, Lucky(), Now));
    }

    [Fact]
    public void PlayCard_ChangesTheConcurrencyVersion()
    {
        var battle = PveBattle.Start(Guid.NewGuid(), Now);
        var before = battle.Version;

        battle.PlayCard(BattleCard.Fireball, Lucky(), Now);

        Assert.NotEqual(before, battle.Version);
    }

    [Fact]
    public void AttachMatch_OnlyAllowedOnceAndOnlyWhenFinished()
    {
        var battle = PveBattle.Start(Guid.NewGuid(), Now);
        Assert.Throws<InvalidOperationException>(() => battle.AttachMatch(Guid.NewGuid()));

        PlayUntilFinished(battle, Lucky);
        battle.AttachMatch(Guid.NewGuid());

        Assert.Throws<InvalidOperationException>(() => battle.AttachMatch(Guid.NewGuid()));
    }

    // Plays Dragon Claw whenever it is ready and Fireball while it recharges.
    private static BattleTurnResult PlayUntilFinished(PveBattle battle, Func<IBattleRandom> random)
    {
        BattleTurnResult result = null!;
        for (var turn = 0; turn < 100 && !battle.IsFinished; turn++)
        {
            var card = battle.CanPlay(BattleCard.DragonClaw) ? BattleCard.DragonClaw : BattleCard.Fireball;
            result = battle.PlayCard(card, random(), Now);
        }

        return battle.IsFinished ? result : throw new InvalidOperationException("The battle never ended.");
    }

    private static BattleTurnResult PlayUntilFinished(PveBattle battle, Func<int[], IBattleRandom> random) =>
        PlayUntilFinished(battle, () => random([]));
}

public class PveBattleLoadoutTests
{
    private static readonly DateTime Now = new(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc);

    private sealed class AlwaysMin : IBattleRandom
    {
        public int Next(int minInclusive, int maxExclusive) => minInclusive;
    }

    [Fact]
    public void AnUpgradedCardHitsHarder()
    {
        var battle = PveBattle.Start(Guid.NewGuid(), Now, new BattleLoadout(3, 1, 1, 0, HeroClass.Paladin));

        var turn = battle.PlayCard(BattleCard.Fireball, new AlwaysMin(), Now);

        Assert.Equal(30, turn.DamageDealt);
        Assert.Equal(PveBattle.BossMaxHp - 30, battle.BossHp);
    }

    [Fact]
    public void AnElixirRaisesStartingAndMaximumHealth()
    {
        var battle = PveBattle.Start(Guid.NewGuid(), Now, new BattleLoadout(1, 2, 1, 25));

        Assert.Equal(125, battle.PlayerHp);
        Assert.Equal(125, battle.PlayerMaxHp);

        // Holy Shield heals 20 at level 2, but never above the raised maximum.
        battle.PlayCard(BattleCard.Fireball, new AlwaysMin(), Now); // boss hits for 15 -> 110
        var turn = battle.PlayCard(BattleCard.HolyShield, new AlwaysMin(), Now);
        Assert.Equal(15, turn.HealthRestored);
        Assert.Equal(125, battle.PlayerHp);
    }

    [Fact]
    public void Start_RejectsACardLevelOutOfRange()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PveBattle.Start(Guid.NewGuid(), Now, new BattleLoadout(4, 1, 1, 0)));
    }
}

public class PveBattleEnrageTests
{
    private static readonly DateTime Now = new(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc);

    private sealed class AlwaysMin : IBattleRandom
    {
        public int Next(int minInclusive, int maxExclusive) => minInclusive;
    }

    [Fact]
    public void TheBossHitsHarderOnceBadlyHurt()
    {
        // Fully upgraded: Dragon Claw 45, Fireball 30. Every roll lands and the boss always Slashes.
        // Paladin, so the class boost (on Holy Shield) doesn't change these numbers.
        var battle = PveBattle.Start(Guid.NewGuid(), Now, new BattleLoadout(3, 1, 3, 0, HeroClass.Paladin));
        battle.PlayCard(BattleCard.DragonClaw, new AlwaysMin(), Now);
        battle.PlayCard(BattleCard.Fireball, new AlwaysMin(), Now);

        Assert.False(battle.IsEnraged);
        Assert.Equal(PveBattle.BossAttackBase + 3 * PveBattle.BossAttackGrowthPerTurn, battle.BossNextAttack);

        battle.PlayCard(BattleCard.DragonClaw, new AlwaysMin(), Now);

        Assert.True(battle.IsEnraged);
        Assert.Equal(PveBattle.BossMaxHp - 120, battle.BossHp);
        var normal = PveBattle.BossAttackBase + 4 * PveBattle.BossAttackGrowthPerTurn;
        Assert.Equal(normal * PveBattle.EnrageDamagePercent / 100, battle.BossNextAttack);
    }
}
