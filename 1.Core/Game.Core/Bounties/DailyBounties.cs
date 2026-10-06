using Game.Core.Entities;

namespace Game.Core.Bounties;

public enum BattleKind
{
    BossFight,
    Duel
}

public enum Bounty
{
    WinDuels,
    FightDuels,
    BeatTheBoss,
    FightTheBoss,
    WinBattles,
    FightBattles
}

/// <param name="Kind">The kind of battle that counts, or null for any battle.</param>
/// <param name="WinsOnly">Only wins count; otherwise every battle fought counts.</param>
public record BountyDefinition(Bounty Bounty, string Name, int Goal, int Reward, BattleKind? Kind, bool WinsOnly)
{
    public bool Counts(BattleKind kind, bool won) => (Kind is null || Kind == kind) && (!WinsOnly || won);
}

/// <summary>A bounty for today and how far the player has got with it.</summary>
public record BountyStatus(BountyDefinition Definition, int Progress)
{
    public bool Completed => Progress >= Definition.Goal;
}

/// <summary>
/// Three goals a day, the same for everyone, that pay gold when completed. They give players a
/// reason to come back and earn, including in the parts of the game they play less.
/// </summary>
public static class DailyBounties
{
    public const int PerDay = 3;

    private static readonly IReadOnlyList<BountyDefinition> Pool =
    [
        new(Bounty.WinDuels, "Win 2 PvP duels", Goal: 2, Reward: 150, BattleKind.Duel, WinsOnly: true),
        new(Bounty.FightDuels, "Fight 3 PvP duels", Goal: 3, Reward: 100, BattleKind.Duel, WinsOnly: false),
        new(Bounty.BeatTheBoss, "Defeat the Shadow Overlord", Goal: 1, Reward: 125, BattleKind.BossFight, WinsOnly: true),
        new(Bounty.FightTheBoss, "Challenge the Shadow Overlord 3 times", Goal: 3, Reward: 75, BattleKind.BossFight, WinsOnly: false),
        new(Bounty.WinBattles, "Win 3 battles of any kind", Goal: 3, Reward: 125, Kind: null, WinsOnly: true),
        new(Bounty.FightBattles, "Fight 5 battles of any kind", Goal: 5, Reward: 100, Kind: null, WinsOnly: false)
    ];

    public static IEnumerable<BountyDefinition> All => Pool;

    public static BountyDefinition Get(Bounty bounty) =>
        Pool.FirstOrDefault(b => b.Bounty == bounty)
        ?? throw new ArgumentOutOfRangeException(nameof(bounty), bounty, "Unknown bounty.");

    /// <summary>Today's bounties. The day picks them, so every player gets the same three and they change at midnight UTC.</summary>
    public static IReadOnlyList<BountyDefinition> For(DateOnly day)
    {
        // A seeded Random gives the same order for the same day on every server.
        var random = new Random(day.DayNumber);
        return Pool.OrderBy(_ => random.Next()).Take(PerDay).ToList();
    }
}

/// <summary>Progress on one of today's bounties. Rows from earlier days are cleared as the player battles.</summary>
public class BountyProgress
{
    public DateOnly Day { get; private set; }
    public Bounty Bounty { get; private set; }
    public int Progress { get; private set; }

    private BountyProgress() { }

    internal BountyProgress(DateOnly day, Bounty bounty)
    {
        Day = day;
        Bounty = bounty;
    }

    internal void Advance() => Progress++;
}

/// <summary>Gold paid on top of a battle's normal reward.</summary>
/// <param name="StreakBonus">Extra gold for winning several battles in a row.</param>
/// <param name="CompletedBounties">Bounties this battle completed; their rewards are already paid.</param>
public record BattleBonuses(int StreakBonus, IReadOnlyList<BountyDefinition> CompletedBounties)
{
    public int Gold => StreakBonus + CompletedBounties.Sum(b => b.Reward);
}
