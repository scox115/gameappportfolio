# 0018. A public status page backed by the API

- **Status:** Accepted
- **Date:** 2026-10-07

## Context

The game has health checks, blue-green releases and monthly restore drills, but only someone with access to Azure or the GitHub Actions logs can see them. Players, and anyone reviewing the project, have no way to tell whether the game is up, what version is running, when it last changed, or whether the backups work.

## Decision

- **`GET /api/v1/status`** (anonymous) returns the same checks as `/health/ready`, the running build (version, commit, Container Apps revision, time awake), the last five releases and the last restore drill. It is output-cached for 30 seconds, so a crowd refreshing during an incident runs at most two health checks a minute.
- Releases and drills are rows in a new **`OperationsEvents`** table:
  - **Releases** are written by the API itself, the first time a revision serves a request on the app's public address (`CONTAINER_APP_NAME.CONTAINER_APP_ENV_DNS_SUFFIX`). The blue-green smoke test uses the revision's own address and health probes reach the container directly, so neither counts, and a build that fails its smoke test is never logged as released. The revision column is unique, so a second replica can't log the same release twice.
  - **Restore drills** are written by the drill (`infra/restore-drill/drill-database.cs record`) into the live database, with a short summary and no error details, because the page is public.
- **`/status`** in the Blazor client shows it all, linked from the version number in the header. It refreshes only when asked, not on a timer.

## Alternatives considered

- **A hosted status service** (Statuspage, Better Stack, Uptime Kuma). These handle uptime history and notifications better, but they cost money or need another server, and none can show this app's releases and drills without more glue.
- **A static `status.json` written by the deploy workflow.** It shows releases without a database table, but it can't show live health, and the monthly drill would need to redeploy the client to update it.
- **Logging releases from the deploy workflow.** The workflow would need its own way into the database (a firewall rule and a token, as the drill has) on every deploy. The API is already connected, and only it knows when players actually reached a revision.
- **Refreshing the page on a timer.** Every check wakes the serverless database. An open tab would keep the database from ever pausing and would use up the free tier's monthly compute.

## Consequences

- Anyone can see the game's health, version and operational history in one click, which also documents what the infrastructure work does.
- Viewing the page wakes the API and the database, like playing does. The cache and the lack of auto-refresh keep that small.
- The page shows the state when it was loaded, not a history of outages. Uptime history would need an external monitor.
- The status endpoint reveals the version, commit and revision names. These are useful to an attacker only alongside a known vulnerability in that exact build, and the repository's dependency scanning and Dependabot cover that.
