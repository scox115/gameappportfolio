using System.Text.Json.Serialization;

namespace Game.Core.Seasons;

/// <summary>
/// A season whose standings are final and rewards paid. Its key is the season, so two API replicas
/// closing the same season at once can't both succeed.
/// </summary>
public class ClosedSeason
{
    private ClosedSeason() { }

    public ClosedSeason(Season season, DateTime closedAt, int rankedHeroes)
    {
        if (closedAt.Kind != DateTimeKind.Utc) throw new ArgumentException("Times are kept in UTC.", nameof(closedAt));
        SeasonStart = season.Start;
        ClosedAt = closedAt;
        RankedHeroes = rankedHeroes;
    }

    public DateOnly SeasonStart { get; private set; }
    [JsonIgnore]
    public Season Season => Season.StartingOn(SeasonStart);
    public DateTime ClosedAt { get; private set; }

    /// <summary>Heroes who fought enough duels to be ranked.</summary>
    public int RankedHeroes { get; private set; }
}
