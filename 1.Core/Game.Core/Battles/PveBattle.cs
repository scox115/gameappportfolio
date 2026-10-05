namespace Game.Core.Battles;

public enum BattleStatus
{
    InProgress,
    Won,
    Lost
}

/// <summary>What happened in one turn, so the client can replay it.</summary>
public record BattleTurnResult(
    int Turn,
    BattleCardDefinition Card,
    int DamageDealt,
    int HealthRestored,
    int? BossDamage,
    BattleStatus Status);

/// <summary>
/// A player-versus-boss battle whose rules run on the server. The client only chooses a card;
/// damage, the boss's attacks and the outcome are decided here.
/// </summary>
public class PveBattle
{
    public const int PlayerMaxHp = 100;
    public const int BossMaxHp = 120;
    public const int OpeningBossAttack = 15;

    public Guid Id { get; private set; }
    public Guid PlayerId { get; private set; }
    public int PlayerHp { get; private set; }
    public int BossHp { get; private set; }
    public int BossNextAttack { get; private set; }
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
            BossNextAttack = OpeningBossAttack,
            Turn = 1,
            Status = BattleStatus.InProgress,
            StartedAt = startedAt,
            Version = Guid.NewGuid()
        };
    }

    public bool IsFinished => Status != BattleStatus.InProgress;

    public BattleTurnResult PlayCard(BattleCard card, IBattleRandom random, DateTime playedAt)
    {
        ArgumentNullException.ThrowIfNull(random);
        if (IsFinished) throw new InvalidOperationException("This battle is already over.");

        var definition = BattleCards.Get(card);
        var turn = Turn;
        Version = Guid.NewGuid();

        // 1. The player's card.
        var damageDealt = Math.Min(definition.Damage, BossHp);
        BossHp -= damageDealt;
        var healthRestored = Math.Min(definition.Heal, PlayerMaxHp - PlayerHp);
        PlayerHp += healthRestored;

        if (BossHp <= 0)
        {
            Finish(BattleStatus.Won, playedAt);
            return new BattleTurnResult(turn, definition, damageDealt, healthRestored, BossDamage: null, Status);
        }

        // 2. The boss strikes back.
        var bossDamage = Math.Min(BossNextAttack, PlayerHp);
        PlayerHp -= bossDamage;

        if (PlayerHp <= 0)
        {
            Finish(BattleStatus.Lost, playedAt);
            return new BattleTurnResult(turn, definition, damageDealt, healthRestored, bossDamage, Status);
        }

        // 3. The boss enrages: +4 per turn plus a random 5-14.
        Turn++;
        BossNextAttack = (Turn * 4) + random.Next(5, 15);

        return new BattleTurnResult(turn, definition, damageDealt, healthRestored, bossDamage, Status);
    }

    public void AttachMatch(Guid matchId)
    {
        if (!IsFinished) throw new InvalidOperationException("Only a finished battle has a match record.");
        if (MatchId is not null) throw new InvalidOperationException("This battle is already settled.");
        MatchId = matchId;
    }

    private void Finish(BattleStatus status, DateTime completedAt)
    {
        Status = status;
        CompletedAt = completedAt;
    }
}
