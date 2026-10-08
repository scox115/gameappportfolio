using Game.Core.Entities;
using Game.Core.Seasons;

namespace Game.Api.Models;

/// <param name="Key">How the season is named in URLs, such as "2026-10".</param>
/// <param name="Name">How the season is named in the game, such as "October 2026".</param>
public record SeasonView(string Key, string Name, DateTime StartsAt, DateTime EndsAt)
{
    public static SeasonView From(Season season) => new(season.Key, season.Name, season.StartsAt, season.EndsAt);
}

/// <param name="Ranks">Which finishing places the reward is for, such as "2nd-3rd".</param>
public record SeasonRewardView(string Ranks, int Gold);

/// <param name="Current">The season under way.</param>
/// <param name="Past">Closed seasons, newest first.</param>
public record SeasonsResponse(SeasonView Current, int DuelsToBeRanked, IReadOnlyList<SeasonRewardView> Rewards, IReadOnlyList<PastSeasonView> Past);

/// <param name="RankedHeroes">Heroes who fought enough duels to be ranked.</param>
public record PastSeasonView(string Key, string Name, int RankedHeroes);

/// <param name="Final">False while the season is under way and the standings can still change.</param>
public record SeasonStandingsResponse(SeasonView Season, bool Final, IReadOnlyList<SeasonStandingRow> Standings);

/// <param name="RewardGold">Gold paid for the finish, or, while the season is under way, what it would pay if the season ended now.</param>
public record SeasonStandingRow(int Rank, Guid Id, string Username, string? Title, string HeroClass, string? AvatarUrl, Cosmetic? Frame,
    int Rating, int Wins, int Losses, int RewardGold);

/// <param name="Rank">Where the hero stands now, or null until they have fought enough duels this season.</param>
/// <param name="LastSeason">How the hero finished the most recent season they dueled in, if any.</param>
public record MySeasonResponse(SeasonView Season, int Rating, int Wins, int Losses, int DuelsToBeRanked, int? Rank, MySeasonResultView? LastSeason);

/// <param name="Rank">Null if the hero fought too few duels to be ranked.</param>
public record MySeasonResultView(string Key, string Name, int? Rank, int Rating, int Wins, int Losses, int RewardGold)
{
    public static MySeasonResultView From(SeasonRecord record) =>
        new(record.Season.Key, record.Season.Name, record.Rank, record.Rating, record.Wins, record.Losses, record.RewardGold);
}
