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

These are estimates. Set a [budget alert](https://learn.microsoft.com/azure/cost-management-billing/costs/tutorial-acm-create-budgets) on the resource group (for example $5) so you hear about any surprise. You can claim only one free SQL database per subscription.

**Trade-offs of staying free:** after about five idle minutes the API scales to zero and the database pauses after an hour. The next visitor waits up to a couple of minutes while both start. There is only ever one API replica, because the PvP lobby lives in memory.

## Security choices

- **No passwords for Azure resources.** The API runs as a user-assigned managed identity. It reaches SQL, blob storage and Key Vault with Entra ID tokens. The SQL server only accepts Entra ID sign-ins, and the storage account doesn't allow account-key access.
- **No Azure credentials in GitHub.** The workflow signs in with OpenID Connect. Only the `production` environment of this repository can sign in as the deploy app, and that app can only change the `rg-card-arena` resource group.
- **Secrets live in Key Vault.** The JWT signing key and RabbitMQ password are passed from GitHub secrets to Key Vault, and the Container App reads them from there.
- The API image is built by the .NET SDK on a chiseled Ubuntu base: no shell, no package manager, running as a non-root user.

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
   - creates the resource group, the API's managed identity and the `card-arena-sql-admins` Entra group (with you and the API's identity in it);
   - creates the deploy app with its GitHub OIDC trust and grants it rights on that resource group only;
   - saves the `AZURE_*` repository variables and generates the `JWT_SIGNING_KEY` and `RABBITMQ_PASSWORD` secrets.

   It's safe to run again. If Windows blocks the script, run `Set-ExecutionPolicy -Scope Process Bypass` first.

3. Let Azure pull the API image. The image goes to GitHub Container Registry as `ghcr.io/scox115/card-arena-api`. Pick one:
   - **Recommended:** after the first deploy, open the package on GitHub (your profile > Packages > card-arena-api > Package settings) and set its visibility to **Public**. Public images are free to download. The image holds only the compiled API and no secrets.
   - Keep the image private: create a classic GitHub token with just the `read:packages` scope and save it as the `GHCR_READ_TOKEN` repository secret. Private downloads count against GitHub's 1 GB a month free transfer, and a cold start can download the image again.

4. Open **Actions > Deploy to Azure > Run workflow**. The run summary links to the live game.

   The first deploy can fail with a Key Vault or storage permission error while the new role assignments spread through Azure. If that happens, run it again.

## Day to day

- **Deploys:** merge to `main`. CI runs first, and the deploy only starts if CI passes.
- **Logs and traces:** in the Azure portal, open the Application Insights resource and use Transaction search or Logs. Container logs are under the Container App's **Log stream**.
- **Health:** open `https://<api>/health/ready`.
- **Query the production database from SSMS:**
  1. Allow your IP address: `az sql server firewall-rule create -g rg-card-arena -s <sql-server-name> -n my-pc --start-ip-address <your-ip> --end-ip-address <your-ip>`.
  2. Connect to `<sql-server-name>.database.windows.net` with **Microsoft Entra MFA** authentication as yourself. You are in the admin group.
- **Tear it all down:** `az group delete --name rg-card-arena`. The Entra group and deploy app stay; delete them in Entra ID if you're done for good.

## Troubleshooting

- **`AADSTS700213: No matching federated identity record found`** when the workflow signs in to Azure:
  the error shows the name GitHub signed in with (for newer repositories it includes numeric IDs, like
  `repo:owner@123/repo@456:environment:production`). Run `infra/setup.ps1` again with the GitHub CLI
  signed in; it trusts both the `owner/repo` and the numeric-ID forms.
- **`RegionDoesNotAllowProvisioning`** for the SQL server: Azure isn't accepting new SQL servers in that
  region for your subscription. The database defaults to `centralus`; to use another region, add a
  repository variable `AZURE_SQL_LOCATION` (for example `westus2` or `northcentralus`) and run the
  workflow again. The rest of the app stays where it is.
