# Deploying to Azure

Every merge to `main` that passes CI is deployed to Azure by [`.github/workflows/deploy.yml`](../.github/workflows/deploy.yml). The infrastructure is defined in [`infra/main.bicep`](../infra/main.bicep), and everything is sized for free or near-free tiers.

```
 Browser ──► Static Web Apps (Free)          Blazor WebAssembly client
    │
    └──────► Container Apps (Consumption)    one replica, scales to zero
               ├─ api        ──► Azure SQL Database (free offer, serverless)   Entra ID sign-in
               │             ──► Storage account (portraits)                   managed identity
               │             ──► Key Vault (JWT key, RabbitMQ password)       managed identity
               │             ──► Application Insights (OpenTelemetry)
               └─ rabbitmq   sidecar, reached on 127.0.0.1:5672
```

## What it costs

| Resource | Tier | Expected cost |
| --- | --- | --- |
| Static Web Apps | Free | $0 |
| Container Apps | Consumption, scale to zero | $0 within the monthly free grant (180,000 vCPU-seconds, about 66 hours of play a month at 0.75 vCPU) |
| Azure SQL Database | [Free offer](https://learn.microsoft.com/azure/azure-sql/database/free-offer): serverless, 100,000 vCore-seconds and 32 GB a month | $0. When the month's allowance runs out, the database pauses instead of billing |
| Storage account | Standard LRS | a few cents |
| Key Vault | Standard | a few cents |
| Log Analytics + Application Insights | Pay as you go, capped at 0.15 GB a day | $0 within the 5 GB monthly free allowance |
| App Configuration (optional, for feature flags) | Free: one store per subscription, 1,000 requests a day | $0 |

These are estimates. Set a [budget alert](https://learn.microsoft.com/azure/cost-management-billing/costs/tutorial-acm-create-budgets) on the resource group (for example $5) so you hear about any surprise. You can claim only one free SQL database per subscription.

**Trade-offs of staying free:** after about five idle minutes the API scales to zero and the database pauses after an hour. The next visitor waits up to a couple of minutes while both start. There is only ever one API replica, because the PvP lobby lives in memory.

## Security choices

- **No passwords for Azure resources.** The API runs as a user-assigned managed identity. It reaches SQL, blob storage and Key Vault with Entra ID tokens. The SQL server only accepts Entra ID sign-ins, and the storage account doesn't allow account-key access.
- **No Azure credentials in GitHub.** The workflow signs in with OpenID Connect. Only the `production` environment of this repository can sign in as the deploy app, and that app can only change the `rg-card-arena` resource group.
- **Secrets live in Key Vault.** The JWT signing key and RabbitMQ password are passed from GitHub secrets to Key Vault, and the Container App reads them from there.
- The API image is built by the .NET SDK on a chiseled Ubuntu base: no shell, no package manager, running as a non-root user.
- **Security headers.** The game is served with a strict Content Security Policy: scripts only from the site itself, network calls only to the site and the API, and images only from the site and the avatar storage account. The deploy fills in those addresses and the hash of the one inline script that `dotnet publish` writes (`infra/set-client-csp.py`), then checks the live site sends the policy. Both the game and the API send HSTS, `nosniff` and anti-framing headers. See [ADR 0015](adr/0015-security-headers.md).

## One-time setup

You need an Azure subscription, the [Azure CLI](https://learn.microsoft.com/cli/azure/install-azure-cli) and, ideally, the [GitHub CLI](https://cli.github.com/). These commands work in Windows PowerShell 5.1.

1. Sign in to both CLIs:

   ```powershell
   az login
   gh auth login
   ```

2. From the repository folder, run the setup script with your subscription id (`az account list --output table` shows it):

   ```powershell
   .\infra\setup.ps1 -SubscriptionId <your-subscription-id>
   ```

   It does the following:
   - creates the resource group, the API's managed identity and the `card-arena-sql-admins` Entra group (with you, the API's identity and the deploy app in it; the deploy app needs it for the restore drill);
   - creates the deploy app with its GitHub OIDC trust and grants it rights on that resource group only;
   - saves the `AZURE_*` repository variables and generates the `JWT_SIGNING_KEY` and `RABBITMQ_PASSWORD` secrets.

   It's safe to run again. If Windows blocks the script, run `Set-ExecutionPolicy -Scope Process Bypass` first.

3. Let Azure pull the API image. The image goes to GitHub Container Registry as `ghcr.io/scox115/card-arena-api`. Pick one:
   - **Recommended:** after the first deploy, open the package on GitHub (your profile > Packages > card-arena-api > Package settings) and set its visibility to **Public**. Public images are free to download. The image holds only the compiled API and no secrets.
   - Keep the image private: create a classic GitHub token with just the `read:packages` scope and save it as the `GHCR_READ_TOKEN` repository secret. Private downloads count against GitHub's 1 GB a month free transfer, and a cold start can download the image again.

4. Open **Actions > Deploy to Azure > Run workflow**. The run summary links to the live game.

   The first deploy can fail with a Key Vault or storage permission error while the new role assignments spread through Azure. If that happens, run it again.

## Day to day

- **Deploys:** merge to `main`. CI runs first, and the deploy only starts if CI passes. When nothing in `infra/main.bicep`, the repository variables or the secrets changed since the last successful deployment, the workflow skips the Bicep deployment and only swaps the API's image, which saves about 3 minutes. To force the full deployment anyway, run **Deploy to Azure** from the Actions tab with **Redeploy the infrastructure** ticked.
- **Blue-green releases:** every deploy creates a new API revision that gets no players at first. The workflow tests it on its own address (`/health/ready` and a real API call), then moves all traffic to it. If the test fails, the new revision is switched off, players stay on the old one, and the run fails. The revision it replaced stays active with no traffic, scaled to zero, so it costs nothing. See [ADR 0016](adr/0016-blue-green-deploys.md).
- **Roll back:** in the Actions tab, run **Roll back the API**. Leave the revision empty to return to the one the last deploy replaced, or enter any revision name from the Container App's **Revisions and replicas** page. It is tested before players are moved. The game client is not rolled back, so this suits a bad API release; if the new client needs the new API, fix forward instead. The next merge to `main` deploys forward as usual.
- **Database changes must work with the previous release.** The new revision applies EF Core migrations when it starts, while players are still on the old one, and a rollback runs old code against the new schema. Add columns and tables in one release and remove the old ones in a later release (expand, then contract), never in the same one.
- **Backups and recovery:** Azure keeps 7 days of point-in-time database backups, and deleted portraits and secrets stay recoverable for 7 days. The **Restore drill** workflow restores the database into a temporary copy on the 1st of every month to prove it works. [disaster-recovery.md](disaster-recovery.md) has the runbooks.
- **Logs and traces:** in the Azure portal, open the Application Insights resource and use Transaction search or Logs. Container logs are under the Container App's **Log stream**.
- **Health:** open `https://<api>/health/ready`.
- **Query the production database from SSMS:**
  1. Allow your IP address: `az sql server firewall-rule create -g rg-card-arena -s <sql-server-name> -n my-pc --start-ip-address <your-ip> --end-ip-address <your-ip>`.
  2. Connect to `<sql-server-name>.database.windows.net` with **Microsoft Entra MFA** authentication as yourself. You are in the admin group.
- **Tear it all down:** `az group delete --name rg-card-arena`. The Entra group and deploy app stay; delete them in Entra ID if you're done for good.

## Custom domain (optional)

The game always answers on its `*.azurestaticapps.net` address. To give it your own address as well:

1. Buy a domain from any registrar if you don't have one. Cloudflare and Porkbun sell `.com` domains for about $10 a year.
2. At your DNS provider, add a **CNAME** record. Use a subdomain such as `play` or `www` as the name, and the
   Static Web App's default host name (the "Game" address in a deploy's summary, without `https://`) as the value.
   On Cloudflare, set the record to **DNS only** (grey cloud).
3. Check that it resolves: `Resolve-DnsName play.example.com` in PowerShell should show the CNAME.
4. In GitHub, add a repository variable `CUSTOM_DOMAIN` with the full name, for example `play.example.com`,
   then run **Deploy to Azure** from the Actions tab. Changing the variable makes the deploy re-apply the infrastructure.
5. Azure validates the record and issues a free certificate that renews itself. HTTPS on the new address can take
   10 to 20 minutes to start working. The API accepts requests from both addresses.

Use a subdomain: a bare domain like `example.com` needs a different kind of validation that this setup doesn't do.

## Feature flags (optional)

Three parts of the game can be switched off without a deploy: `Duels`, `HeroicBoss` and `GoldShop`. While one is off, its button in town is replaced by a "closed for now" note and the API refuses it with a 503. Duels and boss fights already under way play out. All three are on by default (`FeatureManagement` in `appsettings.json`).

To flip them in Azure, the deploy can add a free-tier Azure App Configuration store:

1. Register its resource provider once (the deploy app isn't allowed to): `az provider register --namespace Microsoft.AppConfiguration`. Running `infra/setup.ps1` again does this too.
2. Add a repository variable `APP_CONFIGURATION` with the value `true`, then run **Deploy to Azure**.
3. In the portal, open the `appcs-cardarena-...` App Configuration store, then **Operations > Feature manager > Create**. Name the flag `GoldShop`, `Duels` or `HeroicBoss`, and leave **Enable feature flag** unticked to switch that feature off. Tick it, or delete the flag, to switch it back on.

The API checks the store for changes at most every two minutes while players are using it, so a change shows within a couple of minutes. Flags not in the store keep their `appsettings.json` defaults, and if the store can't be reached the API starts with those defaults. The store accepts Entra ID only (no access keys): the API reads it with its managed identity, and the SQL admins group can edit it.

## Email alerts (optional)

Add a repository variable `ALERT_EMAIL` with your address and run **Deploy to Azure**. The deploy then adds:

- **Server errors:** an email when 5 or more player requests fail with a 5xx status within 15 minutes.
  Application Insights > **Failures** shows which endpoint failed and the exception behind it.
- **Crash loop:** an email when the API container restarts 3 or more times within 15 minutes. Check the
  Container App's **Log stream** and **Revisions**.

Each alert emails again when it resolves. Azure sends a confirmation email when you're added to the alert
group. Together they cost well under $1 a month: about $0.50 for the error check, which runs every
15 minutes, and $0.10 for the restart check. Removing the variable stops new deploys from creating them, but existing rules stay until you delete them in the portal under Monitor > Alerts > Alert rules.

## Troubleshooting

- **`AADSTS700213: No matching federated identity record found`** when the workflow signs in to Azure:
  the error shows the name GitHub signed in with (for newer repositories it includes numeric IDs, like
  `repo:owner@123/repo@456:environment:production`). Run `infra/setup.ps1` again with the GitHub CLI
  signed in; it trusts both the `owner/repo` and the numeric-ID forms.
- **`RegionDoesNotAllowProvisioning`** for the SQL server: Azure isn't accepting new SQL servers in that
  region for your subscription. The database defaults to `centralus`; to use another region, add a
  repository variable `AZURE_SQL_LOCATION` (for example `westus2` or `northcentralus`) and run the
  workflow again. The rest of the app stays where it is.
- **The deploy fails at "Release the new API revision"**: the new build didn't pass its smoke test, and players are still on the previous revision. The log names the revision; open it under the Container App's **Revisions and replicas**, or its **Log stream**, to see why it didn't start (a failing migration is the usual cause). Fix it and merge again.
- **The deploy fails on the custom domain** (`CNAME Record is invalid` or similar): the CNAME record doesn't
  resolve to the Static Web App yet. Check it with `Resolve-DnsName`, wait for DNS to update, and run the workflow
  again. Clear the `CUSTOM_DOMAIN` variable to deploy without it.
- **The deploy shows a warning like `blob-storage is Degraded`**: the game is up, but an optional service isn't. Blob storage only matters for portrait uploads, RabbitMQ only for match history. "Refused the API's identity" means the managed identity is missing its **Storage Blob Data Contributor** role on the storage account (a full deploy, `full: true`, puts it back). "Unreachable" means the storage account couldn't be reached at all; the API's **Log stream** has the exception.
