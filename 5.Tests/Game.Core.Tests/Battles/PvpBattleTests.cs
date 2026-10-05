using Game.Core.Battles;

namespace Game.Core.Tests.Battles;

public class PvpBattleTests
{
    private static readonly DateTime Now = new(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Alice = Guid.NewGuid();
    private static readonly Guid Bob = Guid.NewGuid();

    // Each card rolls once (0-99); a low roll lands it.
    private sealed class FixedRandom(int roll) : IBattleRandom
    {
        public int Next(int minInclusive, int maxExclusive) => Math.Clamp(roll, minInclusive, maxExclusive - 1);
    }

    private static readonly IBattleRandom Lands = new FixedRandom(0);
    private static readonly IBattleRandom Fails = new FixedRandom(99);

    [Fact]
    public void Start_GivesBothPlayersFullHealthAndTheFirstTurnToPlayerOne()
    {
        var battle = PvpBattle.Start(Alice, Bob, Now);

        Assert.Equal(PvpBattle.PlayerMaxHp, battle.HpOf(Alice));
        Assert.Equal(PvpBattle.PlayerMaxHp, battle.HpOf(Bob));
        Assert.Equal(Alice, battle.ActivePlayerId);
        Assert.Equal(Now + PvpBattle.TurnTimeLimit, battle.TurnDeadline);
        Assert.Equal(PvpBattleStatus.InProgress, battle.Status);
    }

    [Fact]
    public void Start_RejectsPlayingYourself()
    {
        Assert.Throws<ArgumentException>(() => PvpBattle.Start(Alice, Alice, Now));
    }

    [Fact]
    public void PlayCard_DamagesTheOpponentAndPassesTheTurn()
    {
        var battle = PvpBattle.Start(Alice, Bob, Now);

        var result = battle.PlayCard(Alice, BattleCard.DragonClaw, Lands, Now.AddSeconds(5));

        Assert.Equal(35, result.DamageDealt);
        Assert.Equal(65, battle.HpOf(Bob));
        Assert.Equal(Bob, battle.ActivePlayerId);
        Assert.Equal(2, battle.Turn);
        Assert.Equal(Now.AddSeconds(5) + PvpBattle.TurnTimeLimit, battle.TurnDeadline);
    }

    [Fact]
    public void PlayCard_RejectsPlayingOutOfTurn()
    {
        var battle = PvpBattle.Start(Alice, Bob, Now);

        Assert.Throws<InvalidOperationException>(() => battle.PlayCard(Bob, BattleCard.Fireball, Lands, Now));
    }

    [Fact]
    public void PlayCard_RejectsOutsiders()
    {
        var battle = PvpBattle.Start(Alice, Bob, Now);

        Assert.Throws<ArgumentException>(() => battle.PlayCard(Guid.NewGuid(), BattleCard.Fireball, Lands, Now));
    }

    [Fact]
    public void PlayCard_CanBeDodged()
    {
        var battle = PvpBattle.Start(Alice, Bob, Now);

        var result = battle.PlayCard(Alice, BattleCard.DragonClaw, Fails, Now);

        Assert.True(result.CardFailed);
        Assert.Equal(PvpBattle.PlayerMaxHp, battle.HpOf(Bob));
        Assert.Equal(Bob, battle.ActivePlayerId);
    }

    [Fact]
    public void HolyShield_BlocksTheOpponentsNextAttack()
    {
        var battle = PvpBattle.Start(Alice, Bob, Now);
        battle.PlayCard(Alice, BattleCard.HolyShield, Lands, Now);
        Assert.True(battle.IsShielded(Alice));

        var result = battle.PlayCard(Bob, BattleCard.DragonClaw, Lands, Now);

        Assert.True(result.AttackBlocked);
        Assert.Equal(0, result.DamageDealt);
        Assert.Equal(PvpBattle.PlayerMaxHp, battle.HpOf(Alice));
        Assert.False(battle.IsShielded(Alice));
    }

    [Fact]
    public void HolyShield_ExpiresWhenItsOwnersTurnComesBack()
    {
        var battle = PvpBattle.Start(Alice, Bob, Now);
        battle.PlayCard(Alice, BattleCard.HolyShield, Lands, Now);
        battle.PlayCard(Bob, BattleCard.HolyShield, Lands, Now);

        battle.PlayCard(Alice, BattleCard.Fireball, Lands, Now);

        Assert.False(battle.IsShielded(Alice));
    }

    [Fact]
    public void HolyShield_CanBeInterrupted()
    {
        var battle = PvpBattle.Start(Alice, Bob, Now);

        var result = battle.PlayCard(Alice, BattleCard.HolyShield, Fails, Now);

        Assert.True(result.CardFailed);
        Assert.False(battle.IsShielded(Alice));
    }

    [Fact]
    public void PlayCard_PowerCardsRechargeForEachPlayerSeparately()
    {
        var battle = PvpBattle.Start(Alice, Bob, Now);
        battle.PlayCard(Alice, BattleCard.DragonClaw, Lands, Now);

        Assert.Equal(BattleCard.DragonClaw, battle.RechargingCardOf(Alice));
        Assert.Null(battle.RechargingCardOf(Bob));

        battle.PlayCard(Bob, BattleCard.DragonClaw, Lands, Now);
        Assert.Throws<InvalidOperationException>(() => battle.PlayCard(Alice, BattleCard.DragonClaw, Lands, Now));
    }

    [Fact]
    public void PlayCard_KnockoutEndsTheBattle()
    {
        var battle = PvpBattle.Start(Alice, Bob, Now);

        PvpTurnResult result = null!;
        while (!battle.IsFinished)
        {
            var player = battle.ActivePlayerId;
            var card = battle.RechargingCardOf(player) == BattleCard.DragonClaw ? BattleCard.Fireball : BattleCard.DragonClaw;
            result = battle.PlayCard(player, card, Lands, Now);
        }

        Assert.True(result.BattleOver);
        Assert.Equal(Alice, battle.WinnerId);
        Assert.Equal(PvpEndReason.Knockout, battle.EndReason);
        Assert.Equal(0, battle.HpOf(Bob));
        Assert.Throws<InvalidOperationException>(() => battle.PlayCard(battle.ActivePlayerId, BattleCard.Fireball, Lands, Now));
    }

    [Fact]
    public void ExpireTurn_ThePlayerWhoRanOutOfTimeLoses()
    {
        var battle = PvpBattle.Start(Alice, Bob, Now);

        Assert.False(battle.ExpireTurn(Now.AddSeconds(29)));
        Assert.True(battle.ExpireTurn(Now + PvpBattle.TurnTimeLimit));

        Assert.Equal(Bob, battle.WinnerId);
        Assert.Equal(PvpEndReason.Timeout, battle.EndReason);
    }

    [Fact]
    public void PlayCard_RejectsALateMove()
    {
        var battle = PvpBattle.Start(Alice, Bob, Now);

        Assert.Throws<InvalidOperationException>(() =>
            battle.PlayCard(Alice, BattleCard.Fireball, Lands, Now + PvpBattle.TurnTimeLimit));
    }

    [Fact]
    public void Forfeit_TheOpponentWins()
    {
        var battle = PvpBattle.Start(Alice, Bob, Now);

        battle.Forfeit(Bob, Now);

        Assert.Equal(Alice, battle.WinnerId);
        Assert.Equal(PvpEndReason.Forfeit, battle.EndReason);
        Assert.Throws<InvalidOperationException>(() => battle.Forfeit(Alice, Now));
    }

    [Fact]
    public void AttachMatch_OnlyAllowedOnceAndOnlyWhenFinished()
    {
        var battle = PvpBattle.Start(Alice, Bob, Now);
        Assert.Throws<InvalidOperationException>(() => battle.AttachMatch(Guid.NewGuid()));

        battle.Forfeit(Alice, Now);
        battle.AttachMatch(Guid.NewGuid());

        Assert.Throws<InvalidOperationException>(() => battle.AttachMatch(Guid.NewGuid()));
    }
}
