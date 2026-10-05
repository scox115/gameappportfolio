namespace Game.Core.Entities;

public class Player
{
    public Guid Id { get; private set; }
    public string Username { get; private set; } = string.Empty;
    public int Gold { get; private set; }
    public int ExperiencePoints { get; private set; }
    public int Level { get; private set; } = 1;
    public string? AvatarUrl { get; private set; }

    // Parameterless constructor required by Entity Framework Core
    private Player() { }

    public Player(string username, int startingGold)
    {
        Id = Guid.NewGuid();
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
    }

    public void DeductGold(int amount)
    {
        if (amount < 0) throw new ArgumentException("Deduction amount must be positive.");
        if (Gold < amount) throw new InvalidOperationException("Insufficient gold balances.");
        Gold -= amount;
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
