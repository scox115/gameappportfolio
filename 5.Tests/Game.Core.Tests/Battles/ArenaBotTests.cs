using Game.Core.Battles;

namespace Game.Core.Tests.Battles;

public class ArenaBotTests
{
    private static readonly Guid Player = Guid.NewGuid();

    // Returns the given values in turn, then the lowest roll.
    private sealed class Rolls(params int[] values) : IBattleRandom
    {
        private readonly Queue<int> _values = new(values);
        public int Next(int minInclusive, int maxExclusive) => _values.Count > 0 ? _values.Dequeue() : minInclusive;
    }

    private static PvpBattle BotGoesFirst() =>
        PvpBattle.Start(ArenaBot.Id, Player, new DateTime(2026, 10, 7, 12, 0, 0, DateTimeKind.Utc));

    private static readonly DateTime Now = new(2026, 10, 7, 12, 0, 1, DateTimeKind.Utc);

    [Fact]
    public void WithBothCardsReady_ItMixesDragonClawAndFireball()
    {
        Assert.Equal(BattleCard.DragonClaw, ArenaBot.ChooseCard(BotGoesFirst(), new Rolls(0)));
        Assert.Equal(BattleCard.Fireball, ArenaBot.ChooseCard(BotGoesFirst(), new Rolls(1)));
    }

    [Fact]
    public void ItFinishesOffAnOpponentFireballCanKnockOut()
    {
        var battle = PvpBattle.Start(Player, ArenaBot.Id, Now.AddSeconds(-1));
        // Whittle the bot's opponent down to 20 health with Dragon Claws and Fireballs that always land.
        var land = new Rolls();
        while (battle.HpOf(Player) > 20)
        {
            battle.PlayCard(Player, BattleCard.Fireball, land, Now);
            var card = battle.HpOf(Player) > 35 && battle.RechargingCardOf(ArenaBot.Id) != BattleCard.DragonClaw ? BattleCard.DragonClaw : BattleCard.Fireball;
            if (battle.HpOf(Player) <= 20) break;
            battle.PlayCard(ArenaBot.Id, card, land, Now);
        }
        if (battle.ActivePlayerId != ArenaBot.Id) battle.PlayCard(Player, BattleCard.Fireball, land, Now);

        Assert.Equal(BattleCard.Fireball, ArenaBot.ChooseCard(battle, new Rolls(0))); // not the riskier Dragon Claw
    }

    [Fact]
    public void ItShieldsItselfWhenHurt_AndBreaksAShieldWithFireball()
    {
        var battle = PvpBattle.Start(Player, ArenaBot.Id, Now.AddSeconds(-1));
        var land = new Rolls();
        battle.PlayCard(Player, BattleCard.DragonClaw, land, Now); // bot at 65
        battle.PlayCard(ArenaBot.Id, BattleCard.Fireball, land, Now);
        battle.PlayCard(Player, BattleCard.Fireball, land, Now); // bot at 45
        battle.PlayCard(ArenaBot.Id, BattleCard.Fireball, land, Now);
        battle.PlayCard(Player, BattleCard.HolyShield, land, Now); // player shielded

        // 45 of 100 health is still above 40%, so it attacks, with Fireball because of the shield.
        Assert.Equal(BattleCard.Fireball, ArenaBot.ChooseCard(battle, new Rolls(0)));

        battle.PlayCard(ArenaBot.Id, BattleCard.Fireball, land, Now); // breaks the shield
        battle.PlayCard(Player, BattleCard.Fireball, land, Now); // bot at 25
        Assert.Equal(BattleCard.HolyShield, ArenaBot.ChooseCard(battle, new Rolls(0)));
    }

    [Fact]
    public void ItNeverPicksARechargingCard()
    {
        var battle = BotGoesFirst();
        var land = new Rolls();
        battle.PlayCard(ArenaBot.Id, BattleCard.DragonClaw, land, Now);
        battle.PlayCard(Player, BattleCard.Fireball, land, Now);

        Assert.Equal(BattleCard.Fireball, ArenaBot.ChooseCard(battle, new Rolls(0)));
    }

    [Fact]
    public void ItOnlyPlaysInItsOwnDuels()
    {
        var battle = PvpBattle.Start(Player, Guid.NewGuid(), Now);
        Assert.Throws<ArgumentException>(() => ArenaBot.ChooseCard(battle, new Rolls()));
    }

    [Fact]
    public void ItBringsBasicCards_AsAClassPickedAtRandom()
    {
        Assert.Equal(BattleLoadout.Basic with { Class = HeroClass.Ranger }, ArenaBot.LoadoutFor(new Rolls(2)));
    }

    [Fact]
    public void ADuelAgainstIt_IsPracticeThatPaysNothing()
    {
        var battle = PvpBattle.Start(Player, ArenaBot.Id, Now, practice: true);
        battle.Forfeit(Player, Now);

        Assert.Equal("Practice duel against the Arena Bot: it doesn't pay rewards or change ratings.",
            DuelRewardRules.NoRewardReason(battle, rewardedDuelsToday: 0));
    }
}
