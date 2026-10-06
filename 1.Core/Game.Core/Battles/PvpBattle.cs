namespace Game.Core.Battles;

public enum PvpBattleStatus
{
    InProgress,
    Finished
}

public enum PvpEndReason
{
    /// <summary>The loser's health reached zero.</summary>
    Knockout,

    /// <summary>The loser didn't play a card before their turn timer ran out.</summary>
    Timeout,

    /// <summary>The loser gave up.</summary>
    Forfeit
}

/// <summary>What one player's card did, so both clients can replay it.</summary>
/// <param name="CardFailed">The opponent resisted, dodged or interrupted the card.</param>
/// <param name="AttackBlocked">The opponent's Holy Shield stopped the attack.</param>
public record PvpTurnResult(
    int Turn,
    Guid PlayerId,
    BattleCardDefinition Card,
    bool CardFailed,
    int DamageDealt,
    int HealthRestored,
    bool AttackBlocked,
    bool BattleOver);

/// <summary>
/// A battle between two players who take turns. The server applies every card; clients only
/// choose which card to play on their own turn.
/// </summary>
/// <remarks>
/// Cards follow the same rules as the boss fight: they can fail, Dragon Claw and Holy Shield
/// need a turn to recharge, and a Holy Shield that lands blocks the opponent's next attack.
/// A player who doesn't act before <see cref="TurnDeadline"/> loses the battle.
/// </remarks>
public class PvpBattle
{
    public const int BasePlayerMaxHp = 100;
    public static readonly TimeSpan TurnTimeLimit = TimeSpan.FromSeconds(30);

    public Guid Id { get; private set; }
    public Guid PlayerOneId { get; private set; }
    public Guid PlayerTwoId { get; private set; }
    public int PlayerOneHp { get; private set; }
    public int PlayerTwoHp { get; private set; }

    // Each player's Gold Shop loadout, fixed when the battle starts: health (base plus any Duel
    // Elixir) and card levels.
    public int PlayerOneMaxHp { get; private set; } = BasePlayerMaxHp;
    public int PlayerTwoMaxHp { get; private set; } = BasePlayerMaxHp;
    public int PlayerOneFireballLevel { get; private set; } = 1;
    public int PlayerOneHolyShieldLevel { get; private set; } = 1;
    public int PlayerOneDragonClawLevel { get; private set; } = 1;
    public int PlayerTwoFireballLevel { get; private set; } = 1;
    public int PlayerTwoHolyShieldLevel { get; private set; } = 1;
    public int PlayerTwoDragonClawLevel { get; private set; } = 1;

    /// <summary>A Holy Shield that will block the player's opponent's next attack.</summary>
    public bool PlayerOneShielded { get; private set; }
    public bool PlayerTwoShielded { get; private set; }

    public BattleCard? PlayerOneLastCard { get; private set; }
    public BattleCard? PlayerTwoLastCard { get; private set; }

    /// <summary>Whose turn it is.</summary>
    public Guid ActivePlayerId { get; private set; }
    public DateTime TurnDeadline { get; private set; }
    public int Turn { get; private set; }

    public PvpBattleStatus Status { get; private set; }
    public Guid? WinnerId { get; private set; }
    public PvpEndReason? EndReason { get; private set; }
    public DateTime StartedAt { get; private set; }
    public DateTime? CompletedAt { get; private set; }

    /// <summary>Gold each player staked on this duel (0 for a friendly duel). Taken when the battle starts.</summary>
    public int Wager { get; private set; }

    /// <summary>A duel between players on the same network: it never pays rewards or moves ratings.</summary>
    public bool Practice { get; private set; }

    /// <summary>The settled match record, set when the battle ends.</summary>
    public Guid? MatchId { get; private set; }

    /// <summary>Changes on every move; used as an optimistic concurrency token.</summary>
    public Guid Version { get; private set; }

    private PvpBattle() { }

    /// <summary>
    /// Starts a battle. <paramref name="firstPlayerId"/> takes the first turn. Each player brings
    /// their own loadout of upgraded cards and elixir health; none means plain level-1 cards.
    /// <paramref name="wager"/> is the gold each player has staked, which the caller takes from them.
    /// </summary>
    public static PvpBattle Start(Guid firstPlayerId, Guid secondPlayerId, DateTime startedAt,
        BattleLoadout? firstLoadout = null, BattleLoadout? secondLoadout = null, int wager = 0, bool practice = false)
    {
        if (practice && wager > 0)
            throw new ArgumentException("A practice duel can't have a wager.", nameof(wager));
        if (!DuelWagers.IsAllowed(wager))
            throw new ArgumentOutOfRangeException(nameof(wager), wager, "That isn't one of the wager amounts.");
        firstLoadout ??= BattleLoadout.Basic;
        secondLoadout ??= BattleLoadout.Basic;
        if (firstPlayerId == Guid.Empty || secondPlayerId == Guid.Empty)
            throw new ArgumentException("Both players need an id.");
        if (firstPlayerId == secondPlayerId)
            throw new ArgumentException("A player cannot battle themselves.");

        if (firstLoadout.BonusHp < 0 || secondLoadout.BonusHp < 0)
            throw new ArgumentException("Bonus health can't be negative.");

        var battle = new PvpBattle
        {
            Id = Guid.NewGuid(),
            PlayerOneId = firstPlayerId,
            PlayerTwoId = secondPlayerId,
            PlayerOneHp = BasePlayerMaxHp + firstLoadout.BonusHp,
            PlayerTwoHp = BasePlayerMaxHp + secondLoadout.BonusHp,
            PlayerOneMaxHp = BasePlayerMaxHp + firstLoadout.BonusHp,
            PlayerTwoMaxHp = BasePlayerMaxHp + secondLoadout.BonusHp,
            PlayerOneFireballLevel = firstLoadout.FireballLevel,
            PlayerOneHolyShieldLevel = firstLoadout.HolyShieldLevel,
            PlayerOneDragonClawLevel = firstLoadout.DragonClawLevel,
            PlayerTwoFireballLevel = secondLoadout.FireballLevel,
            PlayerTwoHolyShieldLevel = secondLoadout.HolyShieldLevel,
            PlayerTwoDragonClawLevel = secondLoadout.DragonClawLevel,
            ActivePlayerId = firstPlayerId,
            TurnDeadline = startedAt + TurnTimeLimit,
            Turn = 1,
            Status = PvpBattleStatus.InProgress,
            StartedAt = startedAt,
            Wager = wager,
            Practice = practice,
            Version = Guid.NewGuid()
        };

        // Fail now on a bad level rather than mid-duel.
        foreach (var card in BattleCards.All)
        {
            battle.CardFor(firstPlayerId, card.Card);
            battle.CardFor(secondPlayerId, card.Card);
        }
        return battle;
    }

    public bool IsFinished => Status == PvpBattleStatus.Finished;

    /// <summary>Cards played so far. The turn counter only moves on when a card doesn't end the battle.</summary>
    public int MovesPlayed => EndReason == PvpEndReason.Knockout ? Turn : Turn - 1;

    /// <summary>The duel was forfeited or timed out before each player made <see cref="DuelRewardRules.MinMovesEach"/> moves.</summary>
    public bool EndedTooEarly =>
        EndReason is PvpEndReason.Forfeit or PvpEndReason.Timeout && MovesPlayed < 2 * DuelRewardRules.MinMovesEach;

    public bool IsParticipant(Guid playerId) => playerId == PlayerOneId || playerId == PlayerTwoId;

    public Guid OpponentOf(Guid playerId) => IsPlayerOne(playerId) ? PlayerTwoId : PlayerOneId;

    public int HpOf(Guid playerId) => IsPlayerOne(playerId) ? PlayerOneHp : PlayerTwoHp;

    public int MaxHpOf(Guid playerId) => IsPlayerOne(playerId) ? PlayerOneMaxHp : PlayerTwoMaxHp;

    public int LevelOf(Guid playerId, BattleCard card)
    {
        var one = IsPlayerOne(playerId);
        return card switch
        {
            BattleCard.Fireball => one ? PlayerOneFireballLevel : PlayerTwoFireballLevel,
            BattleCard.HolyShield => one ? PlayerOneHolyShieldLevel : PlayerTwoHolyShieldLevel,
            BattleCard.DragonClaw => one ? PlayerOneDragonClawLevel : PlayerTwoDragonClawLevel,
            _ => throw new ArgumentOutOfRangeException(nameof(card), card, "Unknown battle card.")
        };
    }

    /// <summary>A card as this player holds it in this battle, upgrades included.</summary>
    public BattleCardDefinition CardFor(Guid playerId, BattleCard card) => BattleCards.AtLevel(card, LevelOf(playerId, card));

    public bool IsShielded(Guid playerId) => IsPlayerOne(playerId) ? PlayerOneShielded : PlayerTwoShielded;

    /// <summary>The player's card that is recharging, if any.</summary>
    public BattleCard? RechargingCardOf(Guid playerId)
    {
        var last = IsPlayerOne(playerId) ? PlayerOneLastCard : PlayerTwoLastCard;
        return last is { } card && BattleCards.Get(card).NeedsRecharge ? card : null;
    }

    public bool IsTurnExpired(DateTime now) => !IsFinished && now >= TurnDeadline;

    public PvpTurnResult PlayCard(Guid playerId, BattleCard card, IBattleRandom random, DateTime playedAt)
    {
        ArgumentNullException.ThrowIfNull(random);
        if (!IsParticipant(playerId)) throw new ArgumentException("Not a participant.", nameof(playerId));
        if (IsFinished) throw new InvalidOperationException("This battle is already over.");
        if (playerId != ActivePlayerId) throw new InvalidOperationException("It's not your turn.");
        if (IsTurnExpired(playedAt)) throw new InvalidOperationException("Your turn timer ran out.");

        var definition = CardFor(playerId, card);
        if (RechargingCardOf(playerId) == card) throw new InvalidOperationException($"{definition.Name} is still recharging.");

        var opponentId = OpponentOf(playerId);
        var turn = Turn;
        Version = Guid.NewGuid();
        SetLastCard(playerId, card);

        // A shield lasts until its owner's next turn, so one that wasn't hit expires now.
        SetShield(playerId, false);

        // Low rolls favour the player, the same as the boss fight.
        var cardFailed = random.Next(0, 100) >= 100 - definition.FailChance;
        var damageDealt = 0;
        var healthRestored = 0;
        var blocked = false;

        if (!cardFailed)
        {
            if (definition.Damage > 0)
            {
                if (IsShielded(opponentId))
                {
                    blocked = true;
                    SetShield(opponentId, false);
                }
                else
                {
                    damageDealt = Math.Min(definition.Damage, HpOf(opponentId));
                    SetHp(opponentId, HpOf(opponentId) - damageDealt);
                }
            }

            healthRestored = Math.Min(definition.Heal, MaxHpOf(playerId) - HpOf(playerId));
            SetHp(playerId, HpOf(playerId) + healthRestored);

            if (definition.BlocksAttack) SetShield(playerId, true);
        }

        if (HpOf(opponentId) <= 0)
        {
            Finish(playerId, PvpEndReason.Knockout, playedAt);
        }
        else
        {
            Turn++;
            ActivePlayerId = opponentId;
            TurnDeadline = playedAt + TurnTimeLimit;
        }

        return new PvpTurnResult(turn, playerId, definition, cardFailed, damageDealt, healthRestored, blocked, IsFinished);
    }

    /// <summary>Ends the battle if the active player ran out of time. Returns true if it did.</summary>
    public bool ExpireTurn(DateTime now)
    {
        if (!IsTurnExpired(now)) return false;

        Version = Guid.NewGuid();
        Finish(OpponentOf(ActivePlayerId), PvpEndReason.Timeout, now);
        return true;
    }

    public void Forfeit(Guid playerId, DateTime now)
    {
        if (!IsParticipant(playerId)) throw new ArgumentException("Not a participant.", nameof(playerId));
        if (IsFinished) throw new InvalidOperationException("This battle is already over.");

        Version = Guid.NewGuid();
        Finish(OpponentOf(playerId), PvpEndReason.Forfeit, now);
    }

    public void AttachMatch(Guid matchId)
    {
        if (!IsFinished) throw new InvalidOperationException("Only a finished battle has a match record.");
        if (MatchId is not null) throw new InvalidOperationException("This battle is already settled.");
        MatchId = matchId;
    }

    private bool IsPlayerOne(Guid playerId)
    {
        if (playerId == PlayerOneId) return true;
        if (playerId == PlayerTwoId) return false;
        throw new ArgumentException("Not a participant.", nameof(playerId));
    }

    private void SetHp(Guid playerId, int hp)
    {
        if (IsPlayerOne(playerId)) PlayerOneHp = hp; else PlayerTwoHp = hp;
    }

    private void SetShield(Guid playerId, bool shielded)
    {
        if (IsPlayerOne(playerId)) PlayerOneShielded = shielded; else PlayerTwoShielded = shielded;
    }

    private void SetLastCard(Guid playerId, BattleCard card)
    {
        if (IsPlayerOne(playerId)) PlayerOneLastCard = card; else PlayerTwoLastCard = card;
    }

    private void Finish(Guid winnerId, PvpEndReason reason, DateTime completedAt)
    {
        Status = PvpBattleStatus.Finished;
        WinnerId = winnerId;
        EndReason = reason;
        CompletedAt = completedAt;
    }
}
