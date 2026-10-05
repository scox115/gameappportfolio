using Game.Core.Battles;

namespace Game.Core.Tests.Battles;

public class PveBattleTests
{
    private static readonly DateTime Now = new(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc);

    private sealed class FixedRandom(int value) : IBattleRandom
    {
        public int Next(int minInclusive, int maxExclusive) => Math.Clamp(value, minInclusive, maxExclusive - 1);
    }

    [Fact]
    public void Start_SetsFullHealthAndTheOpeningBossAttack()
    {
        var playerId = Guid.NewGuid();
        var battle = PveBattle.Start(playerId, Now);

        Assert.Equal(playerId, battle.PlayerId);
        Assert.Equal(PveBattle.PlayerMaxHp, battle.PlayerHp);
        Assert.Equal(PveBattle.BossMaxHp, battle.BossHp);
        Assert.Equal(PveBattle.OpeningBossAttack, battle.BossNextAttack);
        Assert.Equal(1, battle.Turn);
        Assert.Equal(BattleStatus.InProgress, battle.Status);
    }

    [Fact]
    public void Start_RejectsEmptyPlayerId()
    {
        Assert.Throws<ArgumentException>(() => PveBattle.Start(Guid.Empty, Now));
    }

    [Fact]
    public void PlayCard_DamagesBossThenBossStrikesAndEnrages()
    {
        var battle = PveBattle.Start(Guid.NewGuid(), Now);

        var result = battle.PlayCard(BattleCard.Fireball, new FixedRandom(5), Now);

        Assert.Equal(20, result.DamageDealt);
        Assert.Equal(15, result.BossDamage);
        Assert.Equal(100, battle.BossHp);
        Assert.Equal(85, battle.PlayerHp);
        Assert.Equal(2, battle.Turn);
        Assert.Equal(2 * 4 + 5, battle.BossNextAttack);
    }

    [Fact]
    public void PlayCard_HealingIsCappedAtMaxHealth()
    {
        var battle = PveBattle.Start(Guid.NewGuid(), Now);

        var result = battle.PlayCard(BattleCard.HolyShield, new FixedRandom(5), Now);

        Assert.Equal(0, result.HealthRestored);
        Assert.Equal(PveBattle.PlayerMaxHp - PveBattle.OpeningBossAttack, battle.PlayerHp);
    }

    [Fact]
    public void PlayCard_WinningBlowEndsTheBattleBeforeTheBossAttacks()
    {
        var battle = PveBattle.Start(Guid.NewGuid(), Now);

        BattleTurnResult result = null!;
        while (!battle.IsFinished)
        {
            result = battle.PlayCard(BattleCard.DragonClaw, new FixedRandom(5), Now);
        }

        Assert.Equal(BattleStatus.Won, battle.Status);
        Assert.Null(result.BossDamage);
        Assert.Equal(0, battle.BossHp);
        Assert.Equal(Now, battle.CompletedAt);
    }

    [Fact]
    public void PlayCard_PlayerAtZeroHealthLoses()
    {
        var battle = PveBattle.Start(Guid.NewGuid(), Now);

        while (!battle.IsFinished)
        {
            battle.PlayCard(BattleCard.HolyShield, new FixedRandom(14), Now);
        }

        Assert.Equal(BattleStatus.Lost, battle.Status);
        Assert.Equal(0, battle.PlayerHp);
    }

    [Fact]
    public void PlayCard_RejectsMovesAfterTheBattleEnds()
    {
        var battle = PveBattle.Start(Guid.NewGuid(), Now);
        while (!battle.IsFinished)
        {
            battle.PlayCard(BattleCard.DragonClaw, new FixedRandom(5), Now);
        }

        Assert.Throws<InvalidOperationException>(() => battle.PlayCard(BattleCard.Fireball, new FixedRandom(5), Now));
    }

    [Fact]
    public void PlayCard_ChangesTheConcurrencyVersion()
    {
        var battle = PveBattle.Start(Guid.NewGuid(), Now);
        var before = battle.Version;

        battle.PlayCard(BattleCard.Fireball, new FixedRandom(5), Now);

        Assert.NotEqual(before, battle.Version);
    }

    [Fact]
    public void AttachMatch_OnlyAllowedOnceAndOnlyWhenFinished()
    {
        var battle = PveBattle.Start(Guid.NewGuid(), Now);
        Assert.Throws<InvalidOperationException>(() => battle.AttachMatch(Guid.NewGuid()));

        while (!battle.IsFinished)
        {
            battle.PlayCard(BattleCard.DragonClaw, new FixedRandom(5), Now);
        }
        battle.AttachMatch(Guid.NewGuid());

        Assert.Throws<InvalidOperationException>(() => battle.AttachMatch(Guid.NewGuid()));
    }
}
