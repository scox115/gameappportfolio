namespace Game.Core.Battles;

/// <summary>
/// The sparring partner for a player who finds nobody else in the duel lobby: a hero the server plays,
/// in a practice duel that pays nothing and moves no ratings (see docs/adr/0032-arena-bot.md).
/// </summary>
public static class ArenaBot
{
    /// <summary>The bot's hero. It has no sign-in account, so nobody can play as it.</summary>
    public static readonly Guid Id = new("a7e4ab07-0000-4000-8000-000000000001");

    /// <summary>The emoji keeps the name out of reach: hero names can only use letters, numbers and a little punctuation.</summary>
    public const string Name = "🤖 Arena Bot";

    /// <summary>How long the bot "thinks" before it plays, so its moves can be followed.</summary>
    public static readonly TimeSpan ThinkingTime = TimeSpan.FromSeconds(1.2);

    /// <summary>The bot's cards for a duel: basic ones, as a new hero has, with a class picked at random.</summary>
    public static BattleLoadout LoadoutFor(IBattleRandom random)
    {
        ArgumentNullException.ThrowIfNull(random);
        var classes = Enum.GetValues<HeroClass>();
        return BattleLoadout.Basic with { Class = classes[random.Next(0, classes.Length)] };
    }

    /// <summary>
    /// The card the bot plays this turn. It finishes off an opponent Fireball can knock out, shields
    /// itself when it's hurt, breaks a shield with the cheaper Fireball, and otherwise mixes Dragon
    /// Claw's big hits with Fireball's sure ones.
    /// </summary>
    public static BattleCard ChooseCard(PvpBattle battle, IBattleRandom random)
    {
        ArgumentNullException.ThrowIfNull(battle);
        ArgumentNullException.ThrowIfNull(random);
        if (!battle.IsParticipant(Id)) throw new ArgumentException("The Arena Bot isn't in this duel.", nameof(battle));

        var opponent = battle.OpponentOf(Id);
        var recharging = battle.RechargingCardOf(Id);
        bool Ready(BattleCard card) => recharging != card;

        if (!battle.IsShielded(opponent) && battle.CardFor(Id, BattleCard.Fireball).Damage >= battle.HpOf(opponent))
            return BattleCard.Fireball;
        if (Ready(BattleCard.HolyShield) && battle.HpOf(Id) * 5 <= battle.MaxHpOf(Id) * 2) // 40% health or less
            return BattleCard.HolyShield;
        if (battle.IsShielded(opponent))
            return BattleCard.Fireball;
        if (Ready(BattleCard.DragonClaw) && random.Next(0, 2) == 0)
            return BattleCard.DragonClaw;
        return BattleCard.Fireball;
    }
}
