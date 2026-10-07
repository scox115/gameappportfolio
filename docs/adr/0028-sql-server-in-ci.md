# 0028. Concurrency and migration tests run against SQL Server in CI

- **Status:** Accepted
- **Date:** 2026-10-07
- **Supersedes:** the SQL Server checks in [0012](0012-testing-strategy.md), which were manual

## Context

The API tests use EF Core's in-memory provider, which is fast but has no transactions and no real concurrency checks. [0012](0012-testing-strategy.md) left anything that depends on SQL Server to a manual check before merging. Since [0027](0027-scale-out.md), correctness rests on exactly those behaviours: the duel lobby pairs players with optimistic deletes, the outbox relay claims rows with a concurrency token, and two API replicas share one database.

The manual check worked once: running two real API replicas against SQL Server found a lobby race (a player taken by another replica between two reads) that the in-memory and SQLite tests had missed. The next one may not be caught by hand.

## Decision

- **CI runs a SQL Server.** The `build-and-test` job starts the same `mssql/server:2022` image the Docker stack uses, as a service container, and sets `TEST_SQLSERVER` to reach it.
- **Tests that depend on the database engine run on every engine that is available.** `TestDatabase` creates a throwaway database for one test: a SQLite file always, and a SQL Server database when `TEST_SQLSERVER` is set, built by the real migrations and dropped afterwards. The lobby tests (including 40 players joining through two replicas at once), the two-relay outbox test and the two-replica API tests are theories over those engines.
- **The migrations are tested.** A plain test fails when the model has changed without a migration (EF compares it with the migrations' snapshot, no server needed). On SQL Server, a test builds an empty database from every migration, as the first deploy to a new environment does.
- **Locally it's opt-in.** Without `TEST_SQLSERVER` the SQL Server cases are left out (or show as skipped), so `dotnet test` still needs no Docker. Setting the variable to the Docker stack's SQL Server runs them (see `docs/local-development.md`).

## Alternatives considered

- **Testcontainers.** Each test run starts its own container, so nothing has to be configured. But every developer then needs Docker for every test run, and CI already offers service containers for free.
- **Run every API test on SQL Server.** The closest to production, but the suite takes minutes instead of seconds, and most tests (rewards, the shop, moderation) don't depend on the engine. Only the tests whose correctness rests on transactions or concurrency move.
- **Keep the manual check.** It caught one race, but it relies on someone remembering to run it, and on the race happening while they watch.

## Consequences

- A change that breaks pairing, claiming or the migrations on SQL Server fails the pull request. With the race fix from [0027](0027-scale-out.md) undone (and its window widened by a few milliseconds), the 40-player lobby test failed on SQL Server in three runs out of three while passing on SQLite.
- SQL Server starts while the job restores and builds, so it adds little waiting; the SQL Server cases add about ten seconds of tests.
- Cases that need SQL Server are named by engine in the test results, such as `(engine: "SQL Server")` or `(database: "SQL Server")`, so a failure says which engine it happened on.
