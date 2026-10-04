namespace Game.Core.Entities;

public class Player
{
    public Guid Id { get; private set; }
    public string Username { get; private set; } = string.Empty;
    public int Gold { get; private set; }
    public int ExperiencePoints { get; private set; }
    public int Level { get; private set; } = 1;

    // Parameterless constructor required by Entity Framework Core
    private Player() { }

    public Player(string username, int startingGold)
    {
        Id = Guid.NewGuid();
        Username = username;
        Gold = startingGold;
        ExperiencePoints = 0;
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

    public void AddExperience(int amount)
    {
        ExperiencePoints += amount;
        // Simple logic rule: 100 XP per level
        int newLevel = (ExperiencePoints / 100) + 1;
        if (newLevel > Level)
        {
            Level = newLevel;
        }
    }
}
