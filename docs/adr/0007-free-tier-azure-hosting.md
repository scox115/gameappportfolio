# 0007. Host on Azure free tiers, scaling to zero

- **Status:** Accepted
- **Date:** 2026-10-06

## Context

The game must run live in Azure indefinitely on a personal budget, while still using the services a real .NET team would use.

## Decision

| Need | Service | Why |
| --- | --- | --- |
| Blazor client | Static Web Apps Free | Global CDN and TLS at no cost |
| API | Container Apps, Consumption, `minReplicas: 0` | Free monthly grant covers about 66 hours of play; zero cost when idle |
| Database | Azure SQL free offer (serverless, auto-pause) | Real SQL Server, 100,000 vCore-seconds a month free |
| Message broker | RabbitMQ as a sidecar in the API's container app | Same broker as local dev, no extra bill |
| Avatars | Blob Storage, Standard LRS | Cents a month |
| Secrets | Key Vault Standard | Cents a month |
| Telemetry | Application Insights on Log Analytics, capped at 0.15 GB a day | Inside the 5 GB monthly free allowance |

Everything is described in `infra/main.bicep` and deployed by `.github/workflows/deploy.yml` after CI passes on `main`. The workflow skips the Bicep step when nothing in it changed and only swaps the container image.

## Alternatives considered

- **App Service B1.** Always on, so no cold start, but about $13 a month whether anyone plays or not.
- **Azure Service Bus Basic.** Cheap, but a second broker to abstract over ([0003](0003-rewards-synchronous-messaging-for-read-models.md)).
- **Azure Cache for Redis.** No free tier; output caching stays in memory ([0010](0010-output-caching-with-tag-eviction.md)).

## Consequences

- The first visitor after an idle spell waits while the API starts and the database resumes. This is acceptable for a portfolio and documented.
- RabbitMQ in a sidecar has no persistent disk, so a restart loses queued telemetry. Only read models depend on it.
- One API replica ([0006](0006-single-replica-signalr.md)).
