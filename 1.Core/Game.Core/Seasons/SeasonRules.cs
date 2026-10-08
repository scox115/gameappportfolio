using Game.Core.Services;

namespace Game.Core.Seasons;

/// <summary>Who is ranked at the end of a season, what they are paid, and where ratings start again.</summary>
public static class SeasonRules
{
    /// <summary>The first ranked season. Ratings earned before it carry into it as they were.</summary>
    public static readonly Season FirstSeason = Season.StartingOn(new DateOnly(2026, 10, 1));

    /// <summary>Duels a hero must fight in a season to appear in its standings and earn a reward.</summary>
    public const int DuelsToBeRanked = 3;

    /// <summary>
    /// The rating a hero starts the next season with: halfway back to <see cref="EloRating.StartingRating"/>,
    /// so the best stay near the top but everyone has ground to make up.
    /// </summary>
    public static int SoftReset(int rating) =>
        Math.Max(EloRating.MinimumRating, EloRating.StartingRating + (rating - EloRating.StartingRating) / 2);

    /// <summary>Gold paid for finishing a season at <paramref name="rank"/> (1 is first).</summary>
    public static int RewardFor(int rank) => rank switch
    {
        < 1 => throw new ArgumentOutOfRangeException(nameof(rank), rank, "Ranks start at 1."),
        1 => 1000,
        <= 3 => 500,
        <= 10 => 250,
        _ => 50
    };
}
