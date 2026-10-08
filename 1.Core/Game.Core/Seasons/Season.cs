using System.Globalization;

namespace Game.Core.Seasons;

/// <summary>
/// A ranked season: one calendar month, UTC. Ratings are softly reset when a season ends, and the
/// final standings are kept (see docs/adr/0035-ranked-seasons.md).
/// </summary>
public readonly record struct Season
{
    private Season(DateOnly start) => Start = start;

    /// <summary>The first day of the season's month.</summary>
    public DateOnly Start { get; }

    /// <summary>When the season starts, midnight UTC on its first day.</summary>
    public DateTime StartsAt => Start.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);

    /// <summary>When the season ends and the next one starts.</summary>
    public DateTime EndsAt => Next().StartsAt;

    // Methods rather than properties, so serializing a season doesn't walk the calendar forever.
    public Season Next() => new(Start.AddMonths(1));
    public Season Previous() => new(Start.AddMonths(-1));

    /// <summary>How the season is named in the game, such as "October 2026".</summary>
    public string Name => Start.ToString("MMMM yyyy", CultureInfo.InvariantCulture);

    /// <summary>How the season is named in URLs, such as "2026-10".</summary>
    public string Key => Start.ToString("yyyy-MM", CultureInfo.InvariantCulture);

    /// <summary>The season under way at <paramref name="utcNow"/>.</summary>
    public static Season At(DateTime utcNow)
    {
        if (utcNow.Kind != DateTimeKind.Utc) throw new ArgumentException("Times are kept in UTC.", nameof(utcNow));
        return new Season(new DateOnly(utcNow.Year, utcNow.Month, 1));
    }

    /// <summary>The season that starts on <paramref name="start"/>, which must be the first of a month.</summary>
    public static Season StartingOn(DateOnly start)
    {
        if (start.Day != 1) throw new ArgumentException("Seasons start on the first of the month.", nameof(start));
        return new Season(start);
    }

    /// <summary>Reads a key such as "2026-10".</summary>
    public static bool TryParse(string? key, out Season season)
    {
        season = default;
        if (!DateOnly.TryParseExact(key, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start)) return false;
        season = new Season(start);
        return true;
    }

    public override string ToString() => Key;
}
