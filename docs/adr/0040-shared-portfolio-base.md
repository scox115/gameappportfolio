# 0040. Run every portfolio app on one shared Azure base

- **Status:** Accepted
- **Date:** 2026-10-09

## Context

The game is no longer the only app in the portfolio. Portfolio-SLC, the site that links to each project, ran in a second Azure subscription with its own Container Apps environment, Log Analytics workspace and SQL server, all set up by hand, and more apps are planned. Each app repeating that base means another environment to keep current, another workspace to pay for once it passes the free 5 GB, and another free-database allowance spent on a server of its own. The two setups had also drifted apart in region (`eastus2` and `centralus`) and naming, which made comparing them harder than it needed to be.

Kubernetes (AKS) and a container registry were weighed and rejected for apps this size; see below.

## Decision

- **One shared base, in the game's subscription and in `centralus`:** the resource group `rg-portfolio-shared`, created by `infra/setup-shared.ps1` from [`infra/shared.bicep`](../../infra/shared.bicep). It holds a Consumption-plan Container Apps environment, its Log Analytics workspace (capped at 0.15 GB a day) and an Azure SQL server with Entra-only sign-in, administered by the `portfolio-sql-admins` group.
- **Each app keeps what is only its own** in its own resource group: its container app, its storage, Key Vault, Application Insights, static site and identity. Its database is a free serverless database on the shared server (up to 10 per subscription), created by [`infra/modules/database.bicep`](../../infra/modules/database.bicep). The game's are `cardarena` and `cardarena-staging`.
- **The game's template has a shared mode.** When the repository variable `AZURE_SHARED_RESOURCE_GROUP` is set, the deploy reads the shared base's outputs (`infra/shared-base.sh`) and, once the game's database is on the shared server, deploys the API into the shared environment and points it at that database instead of creating a server and an environment of its own. Until the database is there, the deploy keeps the old layout, so a deploy never points the game at an empty database.
- **Moving is a one-off workflow per copy of the game,** **Move to the shared base**, run for staging and then for production. It deletes the old API container app (a container app can't change environment, and the new one has the same name), exports the database to a BACPAC with sqlpackage, creates the new free database, imports the file, checks that every table has the same row count and the same migrations, and then deploys. If any step fails after the new database was created, it is deleted again and the deploy puts the game back where it was. The old database is left untouched, to be deleted by hand once the game works on the shared base.
- **Images stay in GitHub Container Registry.** No Azure Container Registry.
- **The restore drill follows the database:** it reads the server, resource group and database name from the latest deployment.

## Alternatives considered

- **Keep a separate base per app.** Simplest to reason about, but every app pays again for the same pieces and the setups drift.
- **AKS.** The cheapest cluster is a VM that runs all the time, tens of dollars a month before any app runs, plus node upgrades, an ingress controller and certificates to look after. Container Apps runs on Kubernetes already and scales each app to zero for free.
- **One database shared by every app, with a schema each.** Saves nothing, since each free database costs nothing, and couples every app's migrations, backups and restores to the others.
- **Azure Container Registry.** The Basic tier costs about $5 a month; GHCR is free for these images and already in use.
- **Move the database with a database copy.** It can cross servers, but the copy is created as a paid database rather than one of the free ones; creating the free database first and importing into it keeps it free.

## Consequences

- A new app needs only its own resource group, a database module call and the shared resource group's name, not a new environment, workspace and server.
- Apps share the environment's free monthly grant and the subscription's 10 free databases. Heavy use by one app could use up the grant for all of them.
- Moving the game means a few minutes of downtime per copy, and the API's address changes, which the deploy that follows writes into the client.
- The game still scales to zero, so the first visit after a quiet spell still waits for the API to start. That is a separate decision from where it runs.
- The shared base is one more thing that must exist before a deploy. `shared-base.sh` fails the deploy with a message naming `setup-shared.ps1` if it's missing.
