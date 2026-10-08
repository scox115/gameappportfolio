# 0035. Ranked seasons: monthly standings, rewards and a soft reset

- **Status:** Accepted
- **Date:** 2026-10-08
- **Builds on:** [0004](0004-optimistic-concurrency.md) (optimistic concurrency), [0027](0027-scale-out.md) (several replicas), [0010](0010-output-caching-with-tag-eviction.md) (output caching), [0030](0030-guest-play.md) (guests stay off leaderboards)

## Context

The PvP rating only ever went one way for a strong player. Once the top of the leaderboard was settled, newer players had no realistic way to reach it and nobody at the top had a reason to keep playing. Competitive games solve this with seasons: a fixed period with its own standings, a reward for how you finished, and a partial reset so everyone starts the next one with ground to make up.

## Decision

- **A season is a calendar month, UTC.** `Season` in `Game.Core` is worked out from the clock, so there is no seasons table to keep up to date and every replica agrees which season is under way. October 2026 is the first season. Ratings earned before it carry into it unchanged.
- **Each hero carries their season with them.** `Player` stores the season their rating belongs to and their wins and losses in it. `Player.EnterSeason` moves a hero into a new season. It keeps the old season's rating and record as a `SeasonRecord` (if they dueled), moves the rating halfway back to 1000, and starts the season's wins and losses at zero. A duel calls it for both heroes before rating the duel, so the first duel of a month is rated on the reset ratings even if the season worker hasn't run yet.
- **A worker closes finished seasons.** `SeasonWorker` runs on every replica 30 seconds after start-up and then every 5 minutes. It moves every hero still in a finished season into the current one. It ranks each finished season's records by final rating, then wins, then fewest losses. It pays the rewards and adds a `ClosedSeasons` row. All of this is one `SaveChanges`, so it all happens or none of it does.
- **Closing is safe on several replicas.** The `ClosedSeasons` key is the season. Every hero it changes carries the optimistic concurrency token from [ADR 0004](0004-optimistic-concurrency.md). If two replicas close the same season, or a duel finishes while one does, only one save succeeds and the other run tries again later. Rewards are paid exactly once. A test runs two closes at once on SQLite, and on SQL Server in CI, to prove it.
- **Who is ranked and what it pays.** A hero needs 3 duels in the season to be ranked. Guests and the Arena Bot are never ranked. 1st place earns 1,000 gold, 2nd-3rd 500, 4th-10th 250, and every other ranked hero 50.
- **Standings are public and cached.** `GET /api/v1/seasons` lists the current season, the rules and the closed seasons. `GET /api/v1/seasons/{yyyy-MM}/standings` shows the live top 10 for the current season, with what each place would pay now, or a closed season's final top 10 and what each hero earned. Both use the leaderboard output cache, which saves to heroes and season records evict ([ADR 0010](0010-output-caching-with-tag-eviction.md)). `GET /api/v1/seasons/me` shows the signed-in hero's season rating, record, place and how their last season ended.
- **In the game.** The town shows the hero's season card. The leaderboards add a season table with a picker for past seasons, and it reloads with the rest of the page when a match moves the rankings ([ADR 0034](0034-live-lobby.md)).

## Alternatives considered

- **A hard reset to 1000.** The simplest rule, but the first days of every season would pair the best and newest players at random. A halfway reset keeps the order roughly right while still closing gaps.
- **Close seasons with a scheduled Azure job or a Function.** It would run exactly once, but it would be a second deployable with its own identity and configuration. The worker is already deployed with the API, and the database key decides which replica wins.
- **Reset every hero exactly at midnight.** It needs a precise scheduler. Resetting each hero on their first duel of the month, with the worker catching up everyone else within minutes, gives the same result without one.
- **A seasons table edited by an admin.** It allows seasons of any length, but nobody needs that yet. Monthly seasons worked out from the clock can't be misconfigured.

## Consequences

- Players get a fresh race every month and gold for how they finish. The all-time leaderboard and duel records are unchanged.
- The `RankedSeasons` migration adds three columns to `Players` and two small tables. A hero has one `PlayerSeasonRecords` row for each season they dueled in.
- A duel that starts in one month and ends in the next counts in the new season.
- Final standings show heroes as they are now (current name, portrait and class). A hero who deletes their account leaves the past standings with them.
- The account data export includes the hero's season records.
