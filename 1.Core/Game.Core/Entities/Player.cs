using Game.Core.Battles;

namespace Game.Core.Entities;

public class Player
{
    public const int MaxElixirs = 3;
    public const int ElixirBonusHp = 25;

    private readonly List<CardUpgrade> _cardUpgrades = new();

    public Guid Id { get; private set; }
    public string Username { get; private set; } = string.Empty;
    public int Gold { get; private set; }
    public int ExperiencePoints { get; private set; }
    public int Level { get; private set; } = 1;
    public string? AvatarUrl { get; private set; }

    /// <summary>Battle Elixirs bought in the shop; one is drunk at the start of each boss fight.</summary>
    public int Elixirs { get; private set; }

    /// <summary>Cards upgraded in the Gold Shop. A card that isn't listed is level 1.</summary>
    public IReadOnlyCollection<CardUpgrade> CardUpgrades => _cardUpgrades;

    /// <summary>
    /// Changes whenever gold or purchases change; used as an optimistic concurrency token so two
    /// requests can't spend the same gold.
    /// </summary>
    public Guid Version { get; private set; } = Guid.NewGuid();

    // Parameterless constructor required by Entity Framework Core
    private Player() { }

    public Player(string username, int startingGold)
        : this(Guid.NewGuid(), username, startingGold)
    {
    }

    // Used when the player profile shares its id with a sign-in account.
    public Player(Guid id, string username, int startingGold)
    {
        if (id == Guid.Empty) throw new ArgumentException("Player id cannot be empty.", nameof(id));
        Id = id;
        Username = username;
        Gold = startingGold;
        ExperiencePoints = 0;
    }

    public void UpdateAvatar(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) throw new ArgumentException("URL cannot be empty.");
        AvatarUrl = url;
    }

    public void AddGold(int amount)
    {
        if (amount < 0) throw new ArgumentException("Cannot add negative gold.");
        Gold += amount;
        Version = Guid.NewGuid();
    }

    public void DeductGold(int amount)
    {
        if (amount < 0) throw new ArgumentException("Deduction amount must be positive.");
        if (Gold < amount) throw new InvalidOperationException("Insufficient gold balances.");
        Gold -= amount;
        Version = Guid.NewGuid();
    }

    public int CardLevel(BattleCard card) =>
        _cardUpgrades.FirstOrDefault(u => u.Card == card)?.Level ?? 1;

    public void UpgradeCard(BattleCard card)
    {
        if (!Enum.IsDefined(card)) throw new ArgumentOutOfRangeException(nameof(card), card, "Unknown battle card.");
        if (CardLevel(card) >= BattleCards.MaxLevel) throw new InvalidOperationException("This card is already at its top level.");

        if (_cardUpgrades.FirstOrDefault(u => u.Card == card) is { } upgrade)
        {
            upgrade.LevelUp();
        }
        else
        {
            _cardUpgrades.Add(new CardUpgrade(card, 2));
        }
        Version = Guid.NewGuid();
    }

    public void AddElixir()
    {
        if (Elixirs >= MaxElixirs) throw new InvalidOperationException($"You can carry at most {MaxElixirs} elixirs.");
        Elixirs++;
        Version = Guid.NewGuid();
    }

    /// <summary>
    /// What the player brings into a new boss fight: their card levels, plus an elixir's extra
    /// health if they have one, which this uses up.
    /// </summary>
    public PveLoadout TakeLoadoutForBossFight()
    {
        var bonusHp = 0;
        if (Elixirs > 0)
        {
            Elixirs--;
            bonusHp = ElixirBonusHp;
            Version = Guid.NewGuid();
        }

        return new PveLoadout(CardLevel(BattleCard.Fireball), CardLevel(BattleCard.HolyShield), CardLevel(BattleCard.DragonClaw), bonusHp);
    }

    // --- ⭐ MUTATOR METHOD: INCREMENT EXPERIENCE & HANDLE LEVEL UPS ---
    public void AddExperience(int amount)
    {
        if (amount < 0) return;
        ExperiencePoints += amount;

        // Simple RPG leveling formula: every 100 XP grants a Level Up
        if (ExperiencePoints >= Level * 100)
        {
            ExperiencePoints -= (Level * 100);
            Level++;
        }
    }
}
