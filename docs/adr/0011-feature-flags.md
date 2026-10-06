# 0011. Feature flags in configuration, with optional Azure App Configuration

- **Status:** Accepted
- **Date:** 2026-10-06

## Context

If the Gold Shop, duels or the Heroic boss misbehave in production, switching them off should not need a code change and a redeploy.

## Decision

- **`Microsoft.FeatureManagement`** with three flags: `Duels`, `HeroicBoss`, `GoldShop`, all on in `appsettings.json`.
- A switched-off feature answers 503 with a problem+json body (a `RequireFeature` endpoint filter, and a `HubException` from the duel lobby). `GET /api/v1/features` tells the client, which replaces the buttons with a "closed for now" note.
- In Azure, the repository variable `APP_CONFIGURATION=true` adds a free-tier App Configuration store. The API reads it with its managed identity and checks for changes every two minutes, staying inside the free tier's 1,000 requests a day.

## Alternatives considered

- **LaunchDarkly or similar.** Excellent, but commercial.
- **A flags table in SQL.** Would need its own admin UI and caching.

## Consequences

- A feature can be switched off in the portal in seconds, and the app keeps running from `appsettings.json` if App Configuration is unreachable.
- Server and client both check the flag, so a stale client still can't use a switched-off feature.
