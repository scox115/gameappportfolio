namespace Game.Core.Battles;

public enum BattleStatus
{
    InProgress,
    Won,
    Lost
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
    public const int PlayerMaxHp = 100;
    public const int BossMaxHp = 130;
    public const int OpeningBossAttack = 15;

    // The boss hits harder every turn: base + growth per turn + a random 0-5.
    public const int BossAttackBase = 8;
    public const int BossAttackGrowthPerTurn = 2;
    public const int BossAttackVariance = 5;

    // Chance (out of 100) of each move; Slash takes the rest.
    public const int CrushingBlowChance = 25;
    public const int LifeDrainChance = 20;

    public Guid Id { get; private set; }
    public Guid PlayerId { get; private set; }
    public int PlayerHp { get; private set; }
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

    public static PveBattle Start(Guid playerId, DateTime startedAt)
    {
        if (playerId == Guid.Empty) throw new ArgumentException("Player id cannot be empty.", nameof(playerId));

        return new PveBattle
        {
            Id = Guid.NewGuid(),
            PlayerId = playerId,
            PlayerHp = PlayerMaxHp,
            BossHp = BossMaxHp,
            BossNextMove = BossMove.Slash,
            BossNextAttack = OpeningBossAttack,
            Turn = 1,
            Status = BattleStatus.InProgress,
            StartedAt = startedAt,
            Version = Guid.NewGuid()
        };
    }

    public bool IsFinished => Status != BattleStatus.InProgress;

    /// <summary>The card that is recharging this turn, if any.</summary>
    public BattleCard? RechargingCard =>
        LastCardPlayed is { } last && BattleCards.Get(last).NeedsRecharge ? last : null;

    public bool CanPlay(BattleCard card) => !IsFinished && RechargingCard != card;

    public BattleTurnResult PlayCard(BattleCard card, IBattleRandom random, DateTime playedAt)
    {
        ArgumentNullException.ThrowIfNull(random);
        if (IsFinished) throw new InvalidOperationException("This battle is already over.");

        var definition = BattleCards.Get(card);
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
            bossHealed = Math.Min(bossDamage, BossMaxHp - BossHp);
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
        (BossNextMove, BossNextAttack) = RollNextMove(Turn, random);

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

    private static (BossMove Move, int Damage) RollNextMove(int turn, IBattleRandom random)
    {
        var roll = random.Next(0, 100);
        var baseDamage = BossAttackBase + (turn * BossAttackGrowthPerTurn) + random.Next(0, BossAttackVariance + 1);

        if (roll < 100 - CrushingBlowChance - LifeDrainChance) return (BossMove.Slash, baseDamage);
        if (roll < 100 - LifeDrainChance) return (BossMove.CrushingBlow, baseDamage * 8 / 5);
        return (BossMove.LifeDrain, baseDamage * 3 / 4);
    }

    private void Finish(BattleStatus status, DateTime completedAt)
    {
        Status = status;
        CompletedAt = completedAt;
    }
}
