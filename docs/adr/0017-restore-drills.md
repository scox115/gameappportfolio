# 0017. Monthly restore drills for the game database

- **Status:** Accepted
- **Date:** 2026-10-07

## Context

Azure SQL backs up the database automatically: 7 days of point-in-time restore, with a transaction log backup every 5 to 10 minutes. No one had ever restored one, though. So nobody knew whether a restore worked with this setup (Entra-only sign-in, the free offer, local backup storage), how long it took, or whether the copy matched the live data. Without those answers, the recovery time in a runbook would be a guess.

## Decision

- A scheduled workflow (`.github/workflows/restore-drill.yml`) runs on the 1st of every month and can also be started by hand. It restores the live database as it was 10 minutes earlier into a temporary database, compares the copy with the live one, reports the restore time, and deletes the copy.
- The drill is a Python script (`infra/restore-drill/restore_drill.py`) that drives the Azure CLI. It is tested in CI with the CLI and the database faked (`infra/tests/test_restore_drill.py`), covering a good restore, an empty copy, missing tables, missing migration history, a migration that ran after the restore point, a restore point older than the backups, a failed restore that must still clean up, and keeping the copy.
- The copy is read by a .NET 10 file-based app (`infra/restore-drill/inspect-database.cs`, one file and no project) using Microsoft.Data.SqlClient with an Entra ID access token. It returns the EF Core migration history and the row count of every table. The copy passes when its migration history exists and is one the live database has had, its tables match when the migrations do, and it has data whenever the live one does. Row counts may differ, because players keep playing after the restore point.
- To reach the database, the drill opens the firewall for the runner's own IP address only and removes that rule when it finishes. It signs in as the deploy app, which `infra/setup.ps1` now adds to the SQL admin group.
- Portraits get 7-day soft delete for blobs and containers, so an accidental delete or an overwritten portrait can be undone. Key Vault already had soft delete.
- [docs/disaster-recovery.md](../disaster-recovery.md) holds the recovery point and recovery time for each kind of data, plus a runbook for each incident: bad data, a deleted database, a deleted portrait, a deleted secret and a lost resource group.

## Alternatives considered

- **Trust the platform.** Azure documents point-in-time restore well, but a restore that has never been run can still fail on something specific to this setup, such as the firewall, Entra-only sign-in or the free offer. And without a real run there is no measured recovery time.
- **Export a BACPAC to blob storage.** This gives a copy you control, but it repeats backups Azure already takes, a large export would use up the free offer's vCore seconds, and it still says nothing about restoring.
- **Geo-redundant backup storage.** This would survive a region outage, but it costs more than the locally redundant storage that suits a portfolio game. It is recorded as an accepted risk.
- **Check the copy with `sqlcmd` or ODBC.** The runner images don't reliably come with a SQL driver that signs in with a token. The .NET SDK is already installed for the deploy, and a single-file app needs no project.

## Consequences

- A broken backup or restore shows up within a month as a failed run, not during an incident.
- Each run measures the recovery time against the real database, and the runbook points to that number.
- A drill costs a few cents. The copy is a small serverless database that lives for a few minutes and is not on the free offer.
- The deploy app can now sign in to the database. It could already change the SQL server as a Contributor on the resource group, so this grants nothing new in practice, but it is one more identity in the admin group.
- Backups stay in one region. A regional disaster would need a redeploy and would lose the data.
