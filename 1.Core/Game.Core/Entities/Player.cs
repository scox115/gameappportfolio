using Game.Core.Battles;
using Game.Core.Bounties;
using Game.Core.Services;

namespace Game.Core.Entities;

public class Player
{
    public const int MaxElixirs = 3;
    public const int ElixirBonusHp = 25;
    public const int DuelElixirBonusHp = 15;

    private readonly List<CardUpgrade> _cardUpgrades = new();
    private readonly List<OwnedTitle> _titles = new();
    private readonly List<OwnedCosmetic> _cosmetics = new();
    private readonly List<BountyProgress> _bounties = new();

    /// <summary>Extra gold per win in a row after the first, up to <see cref="MaxStreakBonus"/>.</summary>
    public const int StreakBonusPerWin = 10;
    public const int MaxStreakBonus = 50;

    /// <summary>Gold a new hero starts with: enough for one card upgrade or a few elixirs.</summary>
    public const int StartingGold = 200;

    /// <summary>Boss wins per UTC day that pay the full reward; later wins that day pay less.</summary>
    public const int FullRewardBossWinsPerDay = 5;

    public Guid Id { get; private set; }
    public string Username { get; private set; } = string.Empty;
    public int Gold { get; private set; }
    public int ExperiencePoints { get; private set; }
    public int Level { get; private set; } = 1;
    public string? AvatarUrl { get; private set; }

    /// <summary>Battle Elixirs bought in the shop; one is drunk at the start of each boss fight.</summary>
    public int Elixirs { get; private set; }

    /// <summary>Duel Elixirs bought in the shop; one is drunk at the start of each PvP duel.</summary>
    public int DuelElixirs { get; private set; }

    /// <summary>Cards upgraded in the Gold Shop. A card that isn't listed is level 1.</summary>
    public IReadOnlyCollection<CardUpgrade> CardUpgrades => _cardUpgrades;

    /// <summary>PvP duels won; titles in the Gold Shop unlock as this grows.</summary>
    public int PvpWins { get; private set; }

    /// <summary>PvP duels lost.</summary>
    public int PvpLosses { get; private set; }

    /// <summary>Elo-style PvP rating; everyone starts at <see cref="EloRating.StartingRating"/>.</summary>
    public int Rating { get; private set; } = EloRating.StartingRating;

    /// <summary>Titles bought in the Gold Shop.</summary>
    public IReadOnlyCollection<OwnedTitle> Titles => _titles;

    /// <summary>The title shown next to the player's name, if they chose one.</summary>
    public PlayerTitle? EquippedTitle { get; private set; }

    /// <summary>The equipped title as it reads after the name, or null.</summary>
    public string? TitleName => EquippedTitle is { } title ? PlayerTitles.Get(title).Name : null;

    /// <summary>Cosmetics bought in the Gold Shop.</summary>
    public IReadOnlyCollection<OwnedCosmetic> Cosmetics => _cosmetics;

    /// <summary>The avatar frame the player wears, if any.</summary>
    public Cosmetic? EquippedFrame { get; private set; }

    /// <summary>The card skin the player uses, if any.</summary>
    public Cosmetic? EquippedCardSkin { get; private set; }

    /// <summary>Battles won in a row, boss fights and duels alike. A loss resets it.</summary>
    public int WinStreak { get; private set; }

    /// <summary>Boss fights won on <see cref="BossWinsDay"/>, for the daily full-reward limit.</summary>
    public int BossWinsToday { get; private set; }
    public DateOnly? BossWinsDay { get; private set; }

    /// <summary>Progress on today's daily bounties.</summary>
    public IReadOnlyCollection<BountyProgress> Bounties => _bounties;

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

    public void RecordPvpWin(int ratingGained)
    {
        if (ratingGained < 0) throw new ArgumentException("Rating gained cannot be negative.");
        PvpWins++;
        Rating += ratingGained;
        Version = Guid.NewGuid();
    }

    public void RecordPvpLoss(int ratingLost)
    {
        if (ratingLost < 0) throw new ArgumentException("Rating lost cannot be negative.");
        PvpLosses++;
        Rating = Math.Max(EloRating.MinimumRating, Rating - ratingLost);
        Version = Guid.NewGuid();
    }

    public bool OwnsTitle(PlayerTitle title) => _titles.Any(t => t.Title == title);

    /// <summary>Adds a bought title and shows it straight away.</summary>
    public void AddTitle(PlayerTitle title)
    {
        var definition = PlayerTitles.Get(title);
        if (OwnsTitle(title)) throw new InvalidOperationException($"You already own \"{definition.Name}\".");
        if (PvpWins < definition.DuelWinsNeeded)
            throw new InvalidOperationException($"Win {definition.DuelWinsNeeded} duels to unlock \"{definition.Name}\" (you have {PvpWins}).");

        _titles.Add(new OwnedTitle(title));
        EquippedTitle = title;
        Version = Guid.NewGuid();
    }

    /// <summary>Shows an owned title next to the player's name, or none when null.</summary>
    public void EquipTitle(PlayerTitle? title)
    {
        if (title is { } chosen && !OwnsTitle(chosen)) throw new InvalidOperationException("You don't own that title.");
        EquippedTitle = title;
    }

    public bool OwnsCosmetic(Cosmetic cosmetic) => _cosmetics.Any(c => c.Cosmetic == cosmetic);

    /// <summary>Adds a bought cosmetic and puts it on straight away.</summary>
    public void AddCosmetic(Cosmetic cosmetic)
    {
        var definition = CosmeticCatalog.Get(cosmetic);
        if (OwnsCosmetic(cosmetic)) throw new InvalidOperationException($"You already own the {definition.Name}.");

        _cosmetics.Add(new OwnedCosmetic(cosmetic));
        Wear(definition.Kind, cosmetic);
        Version = Guid.NewGuid();
    }

    /// <summary>Wears an owned cosmetic of this kind, or none when null.</summary>
    public void EquipCosmetic(CosmeticKind kind, Cosmetic? cosmetic)
    {
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown cosmetic kind.");
        if (cosmetic is { } chosen)
        {
            if (!OwnsCosmetic(chosen)) throw new InvalidOperationException("You don't own that.");
            if (CosmeticCatalog.Get(chosen).Kind != kind) throw new InvalidOperationException("That doesn't go there.");
        }
        Wear(kind, cosmetic);
    }

    private void Wear(CosmeticKind kind, Cosmetic? cosmetic)
    {
        if (kind == CosmeticKind.AvatarFrame) EquippedFrame = cosmetic;
        else EquippedCardSkin = cosmetic;
    }

    /// <summary>Takes a duel wager's stake. The winner's payout comes back in the match rewards.</summary>
    public void StakeWager(int stake)
    {
        if (!DuelWagers.IsAllowed(stake)) throw new ArgumentOutOfRangeException(nameof(stake), stake, "That isn't one of the wager amounts.");
        if (Gold < stake) throw new InvalidOperationException($"You need {stake} gold to wager that, and you have {Gold}.");
        if (stake > 0) DeductGold(stake);
    }

    /// <summary>Gives back a wager's stake when the duel didn't count.</summary>
    public void RefundWager(int stake)
    {
        if (!DuelWagers.IsAllowed(stake)) throw new ArgumentOutOfRangeException(nameof(stake), stake, "That isn't one of the wager amounts.");
        if (stake > 0) AddGold(stake);
    }

    /// <summary>Today's bounties and the player's progress on each.</summary>
    public IReadOnlyList<BountyStatus> BountiesFor(DateOnly today) =>
        DailyBounties.For(today)
            .Select(b => new BountyStatus(b, _bounties.FirstOrDefault(p => p.Day == today && p.Bounty == b.Bounty)?.Progress ?? 0))
            .ToList();

    /// <summary>
    /// Counts a finished battle towards the win streak and today's bounties, and pays the streak
    /// bonus and any bounties it completes.
    /// </summary>
    /// <param name="payStreakBonus">False when the win still counts towards the streak but pays no bonus, such as a boss win past the daily limit.</param>
    public BattleBonuses RecordBattle(DateOnly today, BattleKind kind, bool won, bool payStreakBonus = true)
    {
        var streakBonus = 0;
        if (won)
        {
            WinStreak++;
            if (payStreakBonus) streakBonus = Math.Min((WinStreak - 1) * StreakBonusPerWin, MaxStreakBonus);
        }
        else
        {
            WinStreak = 0;
        }

        _bounties.RemoveAll(p => p.Day != today);
        var completed = new List<BountyDefinition>();
        foreach (var bounty in DailyBounties.For(today).Where(b => b.Counts(kind, won)))
        {
            var progress = _bounties.FirstOrDefault(p => p.Bounty == bounty.Bounty);
            if (progress is null)
            {
                progress = new BountyProgress(today, bounty.Bounty);
                _bounties.Add(progress);
            }
            if (progress.Progress >= bounty.Goal) continue;

            progress.Advance();
            if (progress.Progress == bounty.Goal) completed.Add(bounty);
        }

        var bonuses = new BattleBonuses(streakBonus, completed);
        if (bonuses.Gold > 0) AddGold(bonuses.Gold);
        Version = Guid.NewGuid();
        return bonuses;
    }

    /// <summary>Boss wins left today that pay the full reward.</summary>
    public int FullRewardBossWinsLeft(DateOnly today) =>
        BossWinsDay == today ? Math.Max(0, FullRewardBossWinsPerDay - BossWinsToday) : FullRewardBossWinsPerDay;

    /// <summary>Counts a boss win. Returns true while it's within today's full-reward limit.</summary>
    public bool RecordBossWin(DateOnly today)
    {
        var withinLimit = FullRewardBossWinsLeft(today) > 0;
        if (BossWinsDay != today)
        {
            BossWinsDay = today;
            BossWinsToday = 0;
        }
        BossWinsToday++;
        return withinLimit;
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

    public void AddDuelElixir()
    {
        if (DuelElixirs >= MaxElixirs) throw new InvalidOperationException($"You can carry at most {MaxElixirs} duel elixirs.");
        DuelElixirs++;
        Version = Guid.NewGuid();
    }

    /// <summary>
    /// What the player brings into a new duel: their card levels, plus a Duel Elixir's extra
    /// health if they have one, which this uses up.
    /// </summary>
    public BattleLoadout TakeLoadoutForDuel()
    {
        var bonusHp = 0;
        if (DuelElixirs > 0)
        {
            DuelElixirs--;
            bonusHp = DuelElixirBonusHp;
            Version = Guid.NewGuid();
        }

        return new BattleLoadout(CardLevel(BattleCard.Fireball), CardLevel(BattleCard.HolyShield), CardLevel(BattleCard.DragonClaw), bonusHp);
    }

    /// <summary>
    /// What the player brings into a new boss fight: their card levels, plus an elixir's extra
    /// health if they have one, which this uses up.
    /// </summary>
    public BattleLoadout TakeLoadoutForBossFight()
    {
        var bonusHp = 0;
        if (Elixirs > 0)
        {
            Elixirs--;
            bonusHp = ElixirBonusHp;
            Version = Guid.NewGuid();
        }

        return new BattleLoadout(CardLevel(BattleCard.Fireball), CardLevel(BattleCard.HolyShield), CardLevel(BattleCard.DragonClaw), bonusHp);
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
