# New portfolio app on the shared base

Everything a new app needs to run next to the game on the shared base ([ADR 0040](../../docs/adr/0040-shared-portfolio-base.md)): its own resource group, container app and, if it wants one, a free database on the shared SQL server. No new environment, registry, workspace or server.

| File | What it does |
| --- | --- |
| [`setup-app.ps1`](setup-app.ps1) | Run once, from a clone of this repository. Creates `rg-<app>`, the managed identity the app runs as, the database (with `-Database`), the GitHub deploy app with OIDC sign-in, and the app repository's variables. |
| [`deploy.yml`](deploy.yml) | Copy to the app repository as `.github/workflows/deploy.yml`. Builds the `Dockerfile`, pushes the image to GitHub Container Registry, creates the container app on the first run and changes only its image after that. |

## Steps

1. The app's repository needs a `Dockerfile` at its root. The container listens on port 8080 (change `TARGET_PORT` in `deploy.yml` if not) and should answer `/health/ready` if Portfolio-SLC is to wake it.
2. Sign in with `az login` and `gh auth login`, then from this repository run:
   ```powershell
   .\templates\new-app\setup-app.ps1 -SubscriptionId <game subscription id> -AppName my-app -GitHubRepo scox115/my-app -Database
   ```
   Leave out `-Database` if the app has no database.
3. Copy `deploy.yml` into the app repository and push to `main`. The run's summary shows the app's address.
4. If the image pull fails, make the GHCR package public, or add a `GHCR_READ_TOKEN` secret (a token that can read packages).
5. To show the app on scottcoxdev.com, add it to Portfolio-SLC's `wwwroot/data/projects.json`, with a `wake` entry pointing at its health check.

## What the app gets

- **Database:** with `-Database`, the app's container gets `ConnectionStrings__Default`, which signs in as its managed identity, so there is no password anywhere. The database is a free serverless one (it pauses after an hour idle; up to 10 per subscription, 2 used by the game and its staging copy). Like the game's identity, the app's joins `portfolio-sql-admins`.
- **Scaling:** 0 to 3 replicas at 0.5 CPU and 1 GB, so it costs nothing while idle and its first request after a quiet spell waits for it to start.
- **A custom domain:** add it by hand with `az containerapp hostname add` and `bind`, as Portfolio-SLC's `infra/move-domain.ps1` does. Later deploys don't touch it.
