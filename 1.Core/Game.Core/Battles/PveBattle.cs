namespace Game.Core.Battles;

public enum BattleStatus
{
    InProgress,
    Won,
    Lost
}

/// <summary>What a player brings into a boss fight or a duel from the Gold Shop.</summary>
public record BattleLoadout(int FireballLevel, int HolyShieldLevel, int DragonClawLevel, int BonusHp,
    HeroClass Class = HeroClass.Sorcerer)
{
    public static readonly BattleLoadout Basic = new(1, 1, 1, 0);
}

/// <summary>What happened in one turn, so the client can replay it.</summary>
/// <param name="CardFailed">The boss resisted, dodged or interrupted the card, so it did nothing.</param>
/// <param name="BossMove">The move the boss used, or null when the player won before it struck.</param>
/// <param name="BossDamage">Damage the boss dealt; 0 when the attack was blocked.</param>
/// <param name="AttackBlocked">Holy Shield stopped the boss's attack.</param>
/// <param name="BossHealed">Health the boss drained back.</param>
public record BattleTurnResult(
    int Turn,
    BattleCardDefinition Card,
    bool CardFailed,
    int DamageDealt,
    int HealthRestored,
    BossMove? BossMove,
    int? BossDamage,
    bool AttackBlocked,
    int BossHealed,
    BattleStatus Status);

/// <summary>
/// A player-versus-boss battle whose rules run on the server. The client only chooses a card;
/// damage, the boss's attacks and the outcome are decided here.
/// </summary>
/// <remarks>
/// The boss announces its next move each turn so the player can plan around it. Every random
/// roll is arranged so a low roll favours the player: a roll of the minimum always lands the
/// card and picks a plain <see cref="BossMove.Slash"/> with no extra damage.
/// </remarks>
public class PveBattle
{
    public const int BasePlayerMaxHp = 100;

    // The normal boss's numbers. The Heroic boss's are in BossProfile.Heroic.
    public const int BossMaxHp = 160;
    public const int OpeningBossAttack = 15;

    // The boss hits harder every turn: base + growth per turn + a random 0-5.
    public const int BossAttackBase = 9;
    public const int BossAttackGrowthPerTurn = 2;
    public const int BossAttackVariance = 5;

    // Below this share of its health the boss is enraged and every move hits harder.
    public const int EnrageBelowPercent = 40;
    public const int EnrageDamagePercent = 150;

    // Chance (out of 100) of each move; Slash takes the rest.
    public const int CrushingBlowChance = 25;
    public const int LifeDrainChance = 20;

    public Guid Id { get; private set; }
    public Guid PlayerId { get; private set; }
    public BossDifficulty Difficulty { get; private set; }

    /// <summary>The player's class, which strengthens one signature card.</summary>
    public HeroClass Class { get; private set; }

    /// <summary>The boss's health, damage and move chances for this battle's difficulty.</summary>
    public BossProfile Boss => BossProfile.For(Difficulty);

    public int PlayerHp { get; private set; }

    /// <summary>Base health plus the class's bonus and any Battle Elixir drunk before the fight.</summary>
    public int PlayerMaxHp { get; private set; }

    // Card levels are fixed when the battle starts, so buying an upgrade mid-fight changes nothing.
    public int FireballLevel { get; private set; } = 1;
    public int HolyShieldLevel { get; private set; } = 1;
    public int DragonClawLevel { get; private set; } = 1;
    public int BossHp { get; private set; }

    /// <summary>The move the boss has announced for this turn.</summary>
    public BossMove BossNextMove { get; private set; }

    /// <summary>The damage the boss's announced move will deal.</summary>
    public int BossNextAttack { get; private set; }

    /// <summary>The card played last turn; cards that need a recharge can't be played again yet.</summary>
    public BattleCard? LastCardPlayed { get; private set; }

    public int Turn { get; private set; }
    public BattleStatus Status { get; private set; }
    public DateTime StartedAt { get; private set; }
    public DateTime? CompletedAt { get; private set; }

    /// <summary>The settled match record, set when the battle ends.</summary>
    public Guid? MatchId { get; private set; }

    /// <summary>Changes on every turn; used as an optimistic concurrency token.</summary>
    public Guid Version { get; private set; }

    private PveBattle() { }

    public static PveBattle Start(Guid playerId, DateTime startedAt, BattleLoadout? loadout = null,
        BossDifficulty difficulty = BossDifficulty.Normal)
    {
        if (playerId == Guid.Empty) throw new ArgumentException("Player id cannot be empty.", nameof(playerId));
        loadout ??= BattleLoadout.Basic;
        if (loadout.BonusHp < 0) throw new ArgumentOutOfRangeException(nameof(loadout), "Bonus health can't be negative.");
        var boss = BossProfile.For(difficulty);

        var maxHp = BasePlayerMaxHp + HeroClasses.Get(loadout.Class).BonusHp + loadout.BonusHp;
        var battle = new PveBattle
        {
            Id = Guid.NewGuid(),
            PlayerId = playerId,
            Difficulty = difficulty,
            Class = loadout.Class,
            PlayerHp = maxHp,
            PlayerMaxHp = maxHp,
            FireballLevel = loadout.FireballLevel,
            HolyShieldLevel = loadout.HolyShieldLevel,
            DragonClawLevel = loadout.DragonClawLevel,
            BossHp = boss.MaxHp,
            BossNextMove = BossMove.Slash,
            BossNextAttack = boss.OpeningAttack,
            Turn = 1,
            Status = BattleStatus.InProgress,
            StartedAt = startedAt,
            Version = Guid.NewGuid()
        };

        // Fail now on a bad level rather than mid-fight.
        foreach (var card in BattleCards.All) battle.CardFor(card.Card);
        return battle;
    }

    public int LevelOf(BattleCard card) => card switch
    {
        BattleCard.Fireball => FireballLevel,
        BattleCard.HolyShield => HolyShieldLevel,
        BattleCard.DragonClaw => DragonClawLevel,
        _ => throw new ArgumentOutOfRangeException(nameof(card), card, "Unknown battle card.")
    };

    /// <summary>The card as the player holds it in this battle, upgrades included.</summary>
    public BattleCardDefinition CardFor(BattleCard card) => HeroClasses.CardFor(Class, card, LevelOf(card));

    public bool IsFinished => Status != BattleStatus.InProgress;

    /// <summary>The boss is badly hurt and fights harder.</summary>
    public bool IsEnraged => BossHp * 100 < Boss.MaxHp * Boss.EnrageBelowPercent;

    /// <summary>The card that is recharging this turn, if any.</summary>
    public BattleCard? RechargingCard =>
        LastCardPlayed is { } last && BattleCards.Get(last).NeedsRecharge ? last : null;

    public bool CanPlay(BattleCard card) => !IsFinished && RechargingCard != card;

    public BattleTurnResult PlayCard(BattleCard card, IBattleRandom random, DateTime playedAt)
    {
        ArgumentNullException.ThrowIfNull(random);
        if (IsFinished) throw new InvalidOperationException("This battle is already over.");

        var definition = CardFor(card);
        if (!CanPlay(card)) throw new InvalidOperationException($"{definition.Name} is still recharging.");

        var turn = Turn;
        Version = Guid.NewGuid();
        LastCardPlayed = card;

        // 1. The player's card, unless the boss resists, dodges or interrupts it.
        var cardFailed = Fails(definition.FailChance, random);
        var damageDealt = 0;
        var healthRestored = 0;
        if (!cardFailed)
        {
            damageDealt = Math.Min(definition.Damage, BossHp);
            BossHp -= damageDealt;
            healthRestored = Math.Min(definition.Heal, PlayerMaxHp - PlayerHp);
            PlayerHp += healthRestored;
        }

        if (BossHp <= 0)
        {
            Finish(BattleStatus.Won, playedAt);
            return new BattleTurnResult(turn, definition, cardFailed, damageDealt, healthRestored,
                BossMove: null, BossDamage: null, AttackBlocked: false, BossHealed: 0, Status);
        }

        // 2. The boss uses the move it announced.
        var move = BossNextMove;
        var blocked = definition.BlocksAttack && !cardFailed;
        var bossDamage = blocked ? 0 : Math.Min(BossNextAttack, PlayerHp);
        PlayerHp -= bossDamage;

        var bossHealed = 0;
        if (move == BossMove.LifeDrain)
        {
            bossHealed = Math.Min(bossDamage, Boss.MaxHp - BossHp);
            BossHp += bossHealed;
        }

        if (PlayerHp <= 0)
        {
            Finish(BattleStatus.Lost, playedAt);
            return new BattleTurnResult(turn, definition, cardFailed, damageDealt, healthRestored,
                move, bossDamage, blocked, bossHealed, Status);
        }

        // 3. The boss announces its next, stronger move.
        Turn++;
        (BossNextMove, BossNextAttack) = RollNextMove(Boss, Turn, random);
        if (IsEnraged) BossNextAttack = BossNextAttack * Boss.EnrageDamagePercent / 100;

        return new BattleTurnResult(turn, definition, cardFailed, damageDealt, healthRestored,
            move, bossDamage, blocked, bossHealed, Status);
    }

    public void AttachMatch(Guid matchId)
    {
        if (!IsFinished) throw new InvalidOperationException("Only a finished battle has a match record.");
        if (MatchId is not null) throw new InvalidOperationException("This battle is already settled.");
        MatchId = matchId;
    }

    // High rolls fail, so the minimum roll always succeeds.
    private static bool Fails(int failChance, IBattleRandom random) =>
        random.Next(0, 100) >= 100 - failChance;

    private static (BossMove Move, int Damage) RollNextMove(BossProfile boss, int turn, IBattleRandom random)
    {
        var roll = random.Next(0, 100);
        var baseDamage = boss.AttackBase + (turn * boss.AttackGrowthPerTurn) + random.Next(0, boss.AttackVariance + 1);

        if (roll < 100 - boss.CrushingBlowChance - boss.LifeDrainChance) return (BossMove.Slash, baseDamage);
        if (roll < 100 - boss.LifeDrainChance) return (BossMove.CrushingBlow, baseDamage * 8 / 5);
        return (BossMove.LifeDrain, baseDamage * 3 / 4);
    }

    private void Finish(BattleStatus status, DateTime completedAt)
    {
        Status = status;
        CompletedAt = completedAt;
    }
}
