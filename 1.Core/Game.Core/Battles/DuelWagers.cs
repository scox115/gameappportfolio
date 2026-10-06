namespace Game.Core.Battles;

/// <summary>
/// Gold both players stake on a duel. The winner takes the pot minus the arena's fee, and the fee
/// leaves the game for good, so every wagered duel drains a little gold from the economy.
/// </summary>
public static class DuelWagers
{
    public const int FeePercent = 10;

    /// <summary>The stakes a player can choose; 0 is a friendly duel.</summary>
    public static readonly IReadOnlyList<int> Stakes = [0, 50, 100, 250, 500];

    public static bool IsAllowed(int stake) => Stakes.Contains(stake);

    /// <summary>The arena's cut of the pot.</summary>
    public static int Fee(int stake) => stake * 2 * FeePercent / 100;

    /// <summary>What the winner collects: both stakes minus the fee.</summary>
    public static int Payout(int stake) => stake * 2 - Fee(stake);
}
