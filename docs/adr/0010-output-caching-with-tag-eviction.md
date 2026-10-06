# 0010. Output caching evicted by EF Core saves, not Redis

- **Status:** Accepted
- **Date:** 2026-10-06

## Context

The leaderboard, player count, arena stats and class list are read far more often than they change, and the database is a serverless free-tier instance billed by vCore-seconds. But a player who just won must see their new gold on the leaderboard at once.

## Decision

- **ASP.NET Core output caching** stores those responses for a minute (classes for an hour). A custom `SameForEveryonePolicy` caches them even when the request carries a token, because the data doesn't depend on who asks.
- **Eviction is driven by the data, not a timer.** `CacheEvictionInterceptor`, an EF Core `SaveChangesInterceptor`, evicts the `players` tag whenever a save touches a player or class record, and `arena-stats` when daily stats change. No endpoint has to remember to clear the cache.
- **The store is in memory by default.** Setting `ConnectionStrings:Redis` switches to Redis with no code change.

## Alternatives considered

- **`IMemoryCache` in each endpoint.** Every write path would need to know which keys to clear.
- **Redis in Azure.** No free tier, and pointless with one replica ([0006](0006-single-replica-signalr.md)).

## Consequences

- Repeated reads skip the database entirely; fresh data still shows immediately.
- Only responses that are identical for every caller may use the shared policy. A player's own profile is never cached.
