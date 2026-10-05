namespace Game.Core.Services;

/// <summary>
/// Elo-style PvP rating: beating a stronger opponent earns more points than beating a weaker
/// one, and the loser drops by the same amount the winner gains.
/// </summary>
public static class EloRating
{
    public const int StartingRating = 1000;
    public const int KFactor = 32;
    public const int MinimumRating = 100;

    /// <summary>The chance, from 0 to 1, that a player rated <paramref name="rating"/> beats <paramref name="opponentRating"/>.</summary>
    public static double ExpectedScore(int rating, int opponentRating) =>
        1.0 / (1.0 + Math.Pow(10, (opponentRating - rating) / 400.0));

    /// <summary>Points the winner gains (and the loser loses). Always at least 1 so a win always counts.</summary>
    public static int PointsForWin(int winnerRating, int loserRating) =>
        Math.Max(1, (int)Math.Round(KFactor * (1 - ExpectedScore(winnerRating, loserRating)), MidpointRounding.AwayFromZero));
}
