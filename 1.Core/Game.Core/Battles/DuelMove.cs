namespace Game.Core.Battles;

/// <summary>
/// One card played in a duel, with both heroes' health and shields straight after it. A duel's moves
/// in order are its replay, played back without re-rolling anything (see docs/adr/0038-match-replays.md).
/// </summary>
public class DuelMove
{
    public long Id { get; private set; }

    public Guid BattleId { get; private set; }

    /// <summary>The duel's turn number when the card was played, from 1.</summary>
    public int Turn { get; private set; }

    public Guid PlayerId { get; private set; }

    public BattleCard Card { get; private set; }

    public bool CardFailed { get; private set; }

    public int DamageDealt { get; private set; }

    public int HealthRestored { get; private set; }

    public bool AttackBlocked { get; private set; }

    public int PlayerOneHp { get; private set; }

    public int PlayerTwoHp { get; private set; }

    public bool PlayerOneShielded { get; private set; }

    public bool PlayerTwoShielded { get; private set; }

    public DateTime PlayedAt { get; private set; }

    private DuelMove() { }

    /// <summary>Records a move just applied to the battle, so the battle holds the state after it.</summary>
    public static DuelMove Record(PvpBattle battle, PvpTurnResult turn, DateTime now) => new()
    {
        BattleId = battle.Id,
        Turn = turn.Turn,
        PlayerId = turn.PlayerId,
        Card = turn.Card.Card,
        CardFailed = turn.CardFailed,
        DamageDealt = turn.DamageDealt,
        HealthRestored = turn.HealthRestored,
        AttackBlocked = turn.AttackBlocked,
        PlayerOneHp = battle.PlayerOneHp,
        PlayerTwoHp = battle.PlayerTwoHp,
        PlayerOneShielded = battle.PlayerOneShielded,
        PlayerTwoShielded = battle.PlayerTwoShielded,
        PlayedAt = now
    };
}
