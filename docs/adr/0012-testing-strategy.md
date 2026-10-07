# 0012. A test pyramid that runs on every pull request

- **Status:** Accepted; the SQL Server checks are superseded by [0028](0028-sql-server-in-ci.md)
- **Date:** 2026-10-05

## Context

Changes land several times a day and every merge to `main` deploys to production. The tests are the only thing standing between a pull request and live players.

## Decision

| Layer | Project | What it uses | Runs |
| --- | --- | --- | --- |
| Domain rules | `Game.Core.Tests` (132) | Plain xUnit, fixed dice and clock | Every PR |
| API and hubs | `Game.Api.Tests` (115) | `WebApplicationFactory`, EF InMemory; SQLite where bulk deletes need a relational engine | Every PR |
| Local orchestration | `Game.AppHost.Tests` (4) | `Aspire.Hosting.Testing`, model only | Every PR |
| Browser | `Game.E2E.Tests` (12) | Playwright Chromium against the real client and API; axe-core WCAG 2.1 AA scans and keyboard-only play | Every PR, separate job |
| Load | `loadtest/arena.js` | k6 against an Azure-sized container with SQL Server and RabbitMQ | On demand, and smoke run on PRs that touch it |

CI also fails on any NuGet package with a known vulnerability, and Dependabot proposes updates weekly.

## Alternatives considered

- **Testcontainers SQL Server for API tests.** Closer to production, but slower and needs Docker on every runner. SQL-specific behaviour (migrations, bulk deletes, concurrency) is checked against a real SQL Server before merging instead.
- **Mocking repositories.** Tests would prove the mocks, not the queries.

## Consequences

- The fast suites run in well under a minute; the browser job adds about three minutes.
- EF InMemory doesn't enforce relational rules, so anything that depends on SQL Server semantics needs a manual check against the Docker database.
