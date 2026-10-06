# 0004. Optimistic concurrency on players and battles

- **Status:** Accepted
- **Date:** 2026-10-05

## Context

Two requests can change the same player at once: a shop purchase and a battle reward, a double-clicked "Buy" button, or both duellists finishing a turn as the timeout worker fires. With last-write-wins, one change silently overwrites the other, which can mint or destroy gold.

## Decision

`Player`, `PveBattle`, `PvpBattle` and `DailyArenaStats` carry a `Version` column configured with `IsConcurrencyToken()`. Domain methods that change state give it a new value. When EF Core's `UPDATE ... WHERE Version = @old` touches no rows, `DbUpdateConcurrencyException` is caught: REST endpoints (shop, class change, boss turns) return 409 Conflict and the client reloads, a duel move fails with a `HubException` asking the player to try again, and starting a duel retries up to three times from fresh data.

## Alternatives considered

- **Pessimistic locks** (`SELECT ... WITH (UPDLOCK)`). Correct, but SQL Server-specific, harder to test with the in-memory provider, and they hold locks across the request.
- **SQL Server `rowversion`.** Equivalent, but an explicit `Version` the domain controls also works on SQLite and EF InMemory in tests.

## Consequences

- No lost updates to gold, XP, rating or battle state.
- Every code path that saves a player must handle a conflict. Under real load it is rare: the load test produced no errors.
