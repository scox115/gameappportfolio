# Disaster recovery

This guide covers what is backed up, how much data and time a recovery can cost, how the backups are tested every month, and the steps for each kind of incident. The commands work in Windows PowerShell 5.1 and assume the default resource group `rg-card-arena`. `az sql server list -g rg-card-arena -o table` shows the SQL server's name.

## What is protected

| What | Protection | Kept for | Data you can lose (RPO) |
|---|---|---|---|
| Game database (`GameDb`, Azure SQL) | Automatic point-in-time backups: full weekly, differential every 12 to 24 hours, transaction log every 5 to 10 minutes | 7 days | About 10 minutes |
| A deleted database | Same backups, restorable after the delete | 7 days | About 10 minutes |
| Player portraits (blob storage) | Soft delete for blobs and containers | 7 days | None, within the 7 days |
| Secrets (Key Vault) | Soft delete | 7 days | None, within the 7 days |
| Code, infrastructure and configuration | Git, Bicep and the repository variables; a deploy rebuilds everything else | Forever | None |

**Recovery time (RTO):** the monthly drill measures how long a restore of the real database takes and writes it in its run summary. That number is the one to plan for. For a database this size it is minutes, not hours.

**Not covered:** the backups are stored in the same region (`requestedBackupStorageRedundancy: 'Local'`, the cheapest option), so they would not survive the loss of a whole Azure region. Geo-redundant backups cost extra, so for a portfolio game a region outage is an accepted risk. See [ADR 0017](adr/0017-restore-drills.md).

## The monthly restore drill

A backup nobody has restored is only a hope. On the 1st of every month, the **Restore drill** workflow (`.github/workflows/restore-drill.yml`) runs `infra/restore-drill/restore_drill.py`. The drill:

1. Restores `GameDb` as it was 10 minutes ago into a new database, `GameDb-drill-<time>`, and times the restore.
2. Lets only the runner's own IP address through the SQL firewall, for the length of the drill.
3. Reads both databases with `infra/restore-drill/inspect-database.cs` and compares their migrations and row counts. The drill fails if the copy has no migration history, is missing tables, or is empty while the live database has data. Small differences in row counts are expected, because players kept playing after the restore point.
4. Deletes the copy and the firewall rule, even if a step failed.

The run summary shows the restore time and a table of live and restored row counts. GitHub emails you when a scheduled run fails. Players are not affected, because the live database is only read. The copy is a small serverless database that exists for a few minutes, so a drill costs a few cents.

To run it by hand, open **Actions > Restore drill > Run workflow**. You can choose how far back to restore and whether to keep the copy so you can look at it in SSMS. If you keep it, delete it afterwards:

```powershell
az sql db delete -g rg-card-arena -s <sql-server-name> -n <copy-name> --yes
```

**One-time setup:** the drill signs in to the database as the deploy app, so the app must be in the `card-arena-sql-admins` group. `infra/setup.ps1` adds it. If you ran the setup script before the drill existed, either run it again or run this:

```powershell
az ad group member add --group card-arena-sql-admins --member-id (az ad sp list --display-name card-arena-github-deploy --query "[0].id" --output tsv)
```

GitHub turns off scheduled workflows in a repository with no activity for 60 days. If that happens, re-enable the drill in the Actions tab.

## Runbooks

### Bad data: a bug or a mistake changed or deleted rows

1. Find out when it happened, in UTC, from Application Insights or the deploy history.
2. Restore a copy from just before that time. Players carry on using the live database meanwhile:

   ```powershell
   az sql db restore -g rg-card-arena -s <sql-server-name> -n GameDb --dest-name GameDb-before --time "2026-10-07T09:30:00" --edition GeneralPurpose --family Gen5 --capacity 1 --compute-model Serverless --min-capacity 0.5 --auto-pause-delay 60 --backup-storage-redundancy Local
   ```

3. If only a few rows are wrong, connect to both databases in SSMS and copy the good rows back. Keep the newer data everyone else created since.
4. If the whole database is wrong, swap the copy in. The API keeps the same connection string, because the database keeps its name:

   ```powershell
   az sql db rename -g rg-card-arena -s <sql-server-name> -n GameDb --new-name GameDb-broken
   az sql db rename -g rg-card-arena -s <sql-server-name> -n GameDb-before --new-name GameDb
   foreach ($r in az containerapp revision list -g rg-card-arena -n ca-cardarena-api --query "[?properties.active].name" -o tsv) { az containerapp revision restart -g rg-card-arena -n ca-cardarena-api --revision $r }
   ```

   The restart matters: open connections still point at the renamed database until the API restarts. Anything players did between the restore point and the swap is lost, so pick the restore point carefully.
5. Delete the copy you no longer need once you're sure.

The restored database is a regular serverless database, not the free offer. It pauses when idle, but it bills for compute while it runs. Check its pricing tier in the portal after a swap.

### The database was deleted

```powershell
az sql db list-deleted -g rg-card-arena -s <sql-server-name> -o table
az sql db restore -g rg-card-arena -s <sql-server-name> -n GameDb --dest-name GameDb --deleted-time "<deletionDate from the list>"
```

The API reconnects by itself once `GameDb` exists again. If it doesn't, restart it with the `foreach` line from the previous runbook. As with any restore, the new database isn't on the free offer.

### A portrait was deleted or replaced

Deleted portraits stay recoverable for 7 days. Undeleting needs a data role on the storage account, such as **Storage Blob Data Contributor**:

```powershell
az storage blob list --account-name <storage-account> --container-name player-avatars --include d --auth-mode login --query "[?deleted].name" -o tsv
az storage blob undelete --account-name <storage-account> --container-name player-avatars --name <blob-name> --auth-mode login
```

The player's profile still points at their new portrait. To show the old one again, put its URL back in their `AvatarUrl` column.

### A secret was deleted from Key Vault

```powershell
az keyvault secret list-deleted --vault-name <vault-name> -o table
az keyvault secret recover --vault-name <vault-name> --name <secret-name>
```

### The whole resource group is gone

Everything except the data can be rebuilt. Run `infra/setup.ps1` again, then **Deploy to Azure** with **Redeploy the infrastructure** ticked. Azure SQL keeps a deleted server's database backups only while the server exists, so deleting the resource group loses the data. That is why `az group delete` is only described under "Tear it all down" in [azure-deployment.md](azure-deployment.md).
