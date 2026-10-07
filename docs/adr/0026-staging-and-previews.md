# 0026. A staging copy every commit reaches first, and previews for pull requests

- **Status:** Accepted
- **Date:** 2026-10-07

## Context

Every merge to `main` went straight to the live game. CI and the browser tests run against an in-memory database and no Azure services, and blue-green releases ([0016](0016-blue-green-deploys.md)) catch an API that won't start, but nothing tried a build against real Azure SQL, Key Vault, managed identity and Static Web Apps before players got it. A change to `infra/main.bicep` was first applied to production. And to see a front-end change in a pull request, a reviewer had to build it locally.

## Decision

- **Staging is a second copy of the whole game, from the same template.** `infra/main.bicep` takes `environmentName` (`production` or `staging`), and staging lives in its own resource group (`rg-card-arena-staging`). Every resource name includes a hash of the resource group, so the copies never collide. Staging has its own database, identity, Key Vault, JWT key and RabbitMQ password: nothing is shared with production except the deploy app and the SQL admin group.
- **Each commit goes to staging first.** `deploy.yml` runs `deploy-environment.yml` twice: for staging, then for production, which starts only if staging deployed, passed its blue-green smoke test and served the client. Production reuses the image staging built and tested, so what reaches players is byte for byte what passed. Adding required reviewers to the production environment in GitHub turns this into a manual approval. A manual run can skip staging for an urgent fix while staging itself is broken.
- **Settings per copy live on GitHub environments.** The `staging` environment holds its own `AZURE_RESOURCE_GROUP`, `AZURE_API_IDENTITY` and secrets; anything not set there falls back to the repository's value. The custom domain, email alerts, App Configuration and recovery emails are production only: App Configuration's free tier allows one store per subscription, and staging doesn't need to page anyone or send email.
- **Pull requests get a preview of the client.** `preview.yml` builds the pull request's client against staging's API and uploads it to staging's Static Web App, which gives each pull request its own address (such as `…-42.eastus2.4.azurestaticapps.net`) and comments the link on the pull request. Closing the pull request deletes the preview. Staging's API accepts calls from these addresses (`Cors:PreviewsOf`), which can't be listed ahead of time, by matching its own Static Web App's name and a pull request number.
- **You can tell staging apart.** Staging and previews show a strip across the top of every page saying it's a test copy whose heroes can be wiped.
- **One-time setup** is the same script with `-Environment staging`, which creates the resource group and identity, trusts the GitHub `staging` environment's sign-in, saves its settings and turns on `STAGING_ENABLED`.

## Alternatives considered

- **Use a Container Apps revision as staging.** Blue-green already gives each build its own address before players move to it. But it shares production's database (so a migration is tested on real data, live), its Key Vault and its settings, and it never tests the Bicep template or the client.
- **Previews of the API too.** Each pull request could get its own labelled API revision in staging. But they would all run their migrations against staging's one database, so one pull request's schema change would break the others. Client previews cover what a reviewer most often wants to see; API changes reach staging once merged and are tested there before production.
- **A staging slot of the Static Web App only (no staging API).** Free, but previews would call the production API, writing test data into the real game, and production's CORS policy would have to accept them.
- **Deploying staging only by hand.** Cheaper in minutes, but a gate that is skipped when busy isn't a gate.

## Consequences

- A bad template change or a build that fails against real Azure stops in staging, and players never see it. Each deploy takes a few minutes longer, because staging runs first.
- Staging costs about as much as production: nothing within the free grants, plus a few cents a month for storage and Key Vault. Azure SQL's free offer allows up to 10 databases per subscription, all in one region, so staging's database must be in the same region as production's (both use `AZURE_SQL_LOCATION`, `centralus` by default). Both API copies draw on the same Container Apps free grant, and staging is idle most of the time.
- The Free Static Web Apps plan holds three previews at a time. A fourth open pull request's preview fails until another closes; the pull request itself isn't blocked, since previews aren't a required check.
- Staging, its API and the previews are public, like the game. They hold only test heroes; nothing from production is ever copied into them.
- Previews of pull requests from forks or Dependabot aren't built, because those runs get no access to Azure.
