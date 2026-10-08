using System.Text.Json.Serialization;

namespace Game.Core.Seasons;

/// <summary>How a hero finished a past season: kept when their rating is reset for the next one.</summary>
public class SeasonRecord
{
    private SeasonRecord() { }

    internal SeasonRecord(DateOnly seasonStart, int rating, int wins, int losses)
    {
        SeasonStart = seasonStart;
        Rating = rating;
        Wins = wins;
        Losses = losses;
    }

    public DateOnly SeasonStart { get; private set; }
    [JsonIgnore]
    public Season Season => Season.StartingOn(SeasonStart);

    /// <summary>The hero's rating when the season ended.</summary>
    public int Rating { get; private set; }

    public int Wins { get; private set; }
    public int Losses { get; private set; }
    public int Duels => Wins + Losses;

    /// <summary>Where the hero finished, or null if they fought too few duels to be ranked.</summary>
    public int? Rank { get; private set; }

    /// <summary>Gold paid for the finish.</summary>
    public int RewardGold { get; private set; }

    /// <summary>True once the season has been closed and this record ranked.</summary>
    public bool Settled { get; private set; }

    internal void Settle(int? rank, int rewardGold)
    {
        if (Settled) throw new InvalidOperationException("This season has already been settled.");
        Rank = rank;
        RewardGold = rewardGold;
        Settled = true;
    }
}
