namespace Game.Core.Battles;

/// <summary>
/// When a finished duel pays rewards and moves ratings. These rules stop players farming gold
/// and rating by duelling a second account of their own.
/// </summary>
public static class DuelRewardRules
{
    /// <summary>A duel that ends by forfeit or timeout counts only after each player has made this many moves.</summary>
    public const int MinMovesEach = 2;

    /// <summary>Duels a day against the same opponent that pay rewards; later ones that day don't count.</summary>
    public const int RewardedDuelsPerOpponentPerDay = 3;

    /// <summary>
    /// Why this duel pays no rewards, or null when it counts. A duel that doesn't count refunds any wager.
    /// </summary>
    /// <param name="rewardedDuelsToday">Duels the same two players have already been rewarded for today.</param>
    public static string? NoRewardReason(PvpBattle battle, int rewardedDuelsToday)
    {
        ArgumentNullException.ThrowIfNull(battle);
        if (!battle.IsFinished) throw new InvalidOperationException("The duel isn't over yet.");

        if (battle.Practice)
            return "Practice duel: you're on the same network as your opponent, so it doesn't pay rewards or change ratings.";
        if (battle.EndedTooEarly)
            return $"The duel ended before both players made {MinMovesEach} moves, so it doesn't count. Any wager was refunded.";
        if (rewardedDuelsToday >= RewardedDuelsPerOpponentPerDay)
            return $"You've already had {RewardedDuelsPerOpponentPerDay} rewarded duels against this opponent today, so this one doesn't count. Any wager was refunded.";
        return null;
    }
}
