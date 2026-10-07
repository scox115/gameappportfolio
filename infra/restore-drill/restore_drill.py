#!/usr/bin/env python3
"""Restore drill: proves the live database can be brought back from its automatic backups.

Azure SQL keeps point-in-time backups of the database (7 days by default). A backup nobody has
restored is only a hope, so this script restores the live database as it was a few minutes ago into
a new, temporary database, checks that the copy holds the same schema and data, records how long the
restore took, and then deletes the copy. Players are not affected: the live database is only read.

    restore_drill.py --resource-group rg-card-arena [--minutes-ago 10] [--keep]

It needs the Azure CLI signed in as an admin of the SQL server (the deploy app, see
docs/disaster-recovery.md) and the .NET SDK, which runs inspect-database.cs next to this file.
See docs/adr/0017-restore-drills.md.
"""
import argparse
import datetime as dt
import json
import os
import subprocess
import sys
import time
import urllib.request
from pathlib import Path

INSPECT = Path(__file__).with_name("inspect-database.cs")
SQL_RESOURCE = "https://database.windows.net/"


def log(message):
    print(message, file=sys.stderr, flush=True)


def az(*args):
    """Runs an Azure CLI command and returns its JSON output (None when it prints nothing)."""
    result = subprocess.run(["az", *args, "--output", "json"], capture_output=True, text=True)
    if result.returncode != 0:
        raise RuntimeError(f"az {' '.join(args)} failed: {result.stderr.strip()}")
    return json.loads(result.stdout) if result.stdout.strip() else None


def public_ip():
    """The address this machine reaches Azure from, so the drill can let only itself through the firewall."""
    with urllib.request.urlopen("https://api.ipify.org", timeout=20) as response:
        return response.read().decode().strip()


def inspect_database(server_fqdn, database, token):
    """Migrations and per-table row counts of one database, read by inspect-database.cs."""
    result = subprocess.run(
        ["dotnet", "run", str(INSPECT), "--", server_fqdn, database],
        capture_output=True, text=True, env={**os.environ, "SQL_ACCESS_TOKEN": token})
    if result.returncode != 0:
        raise RuntimeError(f"Couldn't read {database}: {result.stderr.strip() or result.stdout.strip()}")
    return json.loads(result.stdout.strip().splitlines()[-1])


def now():
    return dt.datetime.now(dt.timezone.utc)


def find_server(group):
    """The SQL server of the latest successful deployment (the same place the deploy workflow reads it)."""
    fqdn = az("deployment", "group", "list", "--resource-group", group, "--query",
              "sort_by([?starts_with(name, 'card-arena-') && properties.provisioningState == 'Succeeded'], "
              "&properties.timestamp)[-1].properties.outputs.sqlServer.value")
    if not fqdn:
        raise RuntimeError(f"No successful card-arena deployment in {group}; deploy the game first.")
    return fqdn


def compare(live, restored):
    """Problems that mean the restored copy is not a usable database; an empty list means it passed.

    Row counts may differ: players kept playing after the restore point. A migration the live database
    has and the copy lacks is fine too, if it ran after the restore point, so only gaps the other way
    (or an empty copy) count as problems.
    """
    problems = []
    if not restored["migrations"]:
        problems.append("The restored database has no migration history.")
    elif restored["migrations"][-1] not in live["migrations"]:
        problems.append(f"The restored database is on migration {restored['migrations'][-1]}, which the live one never had.")
    missing = sorted(set(live["tables"]) - set(restored["tables"]))
    if missing and restored["migrations"] == live["migrations"]:
        problems.append(f"Tables missing from the restored database: {', '.join(missing)}.")
    if sum(live["tables"].values()) > 0 and sum(restored["tables"].values()) == 0:
        problems.append("The restored database is empty, but the live one has data.")
    return problems


def summary(server, database, drill, restore_point, seconds, live, restored, problems):
    lines = [
        f"### {'✅ Restore drill passed' if not problems else '❌ Restore drill failed'}",
        "",
        f"- Restored `{database}` on `{server}` as it was at **{restore_point:%Y-%m-%d %H:%M} UTC** into `{drill}`.",
        f"- The restore took **{seconds / 60:.1f} minutes** (the recovery time to plan for).",
        f"- Migrations: live is on `{(live['migrations'] or ['none'])[-1]}`, the copy on `{(restored['migrations'] or ['none'])[-1]}`.",
        "",
        "| Table | Live rows | Restored rows |",
        "|---|---:|---:|",
    ]
    for table in sorted(set(live["tables"]) | set(restored["tables"])):
        lines.append(f"| {table} | {live['tables'].get(table, '-')} | {restored['tables'].get(table, '-')} |")
    if problems:
        lines += [""] + [f"- ❌ {problem}" for problem in problems]
    return "\n".join(lines) + "\n"


def run(group, database, minutes_ago, keep, server=None):
    fqdn = server or find_server(group)
    server_name = fqdn.split(".")[0]
    started_at = now()
    restore_point = (started_at - dt.timedelta(minutes=minutes_ago)).replace(second=0, microsecond=0)

    live_db = az("sql", "db", "show", "--resource-group", group, "--server", server_name, "--name", database)
    earliest = dt.datetime.fromisoformat(live_db["earliestRestoreDate"].replace("Z", "+00:00"))
    if restore_point < earliest:
        raise RuntimeError(f"Backups only go back to {earliest:%Y-%m-%d %H:%M} UTC; try a smaller --minutes-ago.")

    stamp = f"{started_at:%Y%m%d%H%M}"
    drill = f"{database}-drill-{stamp}"
    rule = f"restore-drill-{stamp}"
    ip = public_ip()
    log(f"Letting {ip} through the SQL firewall for the drill.")
    az("sql", "server", "firewall-rule", "create", "--resource-group", group, "--server", server_name,
       "--name", rule, "--start-ip-address", ip, "--end-ip-address", ip)
    restore_started = False
    try:
        log(f"Restoring {database} as of {restore_point:%Y-%m-%d %H:%M} UTC into {drill}...")
        clock = time.monotonic()
        # The copy is a small serverless database that pauses itself, so a forgotten one costs next to nothing.
        restore_started = True
        az("sql", "db", "restore", "--resource-group", group, "--server", server_name, "--name", database,
           "--dest-name", drill, "--time", f"{restore_point:%Y-%m-%dT%H:%M:%S}",
           "--edition", "GeneralPurpose", "--family", "Gen5", "--capacity", "1",
           "--compute-model", "Serverless", "--min-capacity", "0.5", "--auto-pause-delay", "60",
           "--backup-storage-redundancy", "Local")
        seconds = time.monotonic() - clock
        log(f"Restored in {seconds / 60:.1f} minutes. Comparing it with the live database...")

        token = az("account", "get-access-token", "--resource", SQL_RESOURCE, "--query", "accessToken")
        live = inspect_database(fqdn, database, token)
        restored = inspect_database(fqdn, drill, token)
        problems = compare(live, restored)
        return problems, summary(server_name, database, drill, restore_point, seconds, live, restored, problems)
    finally:
        if restore_started and not keep:
            log(f"Deleting {drill}.")
            try:
                az("sql", "db", "delete", "--resource-group", group, "--server", server_name, "--name", drill, "--yes")
            except RuntimeError as error:
                log(f"::warning::Couldn't delete {drill}; delete it in the portal. {error}")
        elif keep:
            log(f"Kept {drill}; delete it when you're done.")
        try:
            az("sql", "server", "firewall-rule", "delete", "--resource-group", group, "--server", server_name, "--name", rule)
        except RuntimeError as error:
            log(f"::warning::Couldn't remove the firewall rule {rule}; remove it in the portal. {error}")


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--resource-group", required=True)
    parser.add_argument("--database", default="GameDb")
    parser.add_argument("--server", help="the SQL server's address (default: from the latest deployment)")
    parser.add_argument("--minutes-ago", type=int, default=10, help="how far back to restore (default 10)")
    parser.add_argument("--keep", action="store_true", help="keep the restored copy to look at")
    args = parser.parse_args(argv)

    try:
        problems, report = run(args.resource_group, args.database, args.minutes_ago, args.keep, args.server)
    except RuntimeError as error:
        log(f"::error::{error}")
        return 1
    print(report)
    summary_file = os.environ.get("GITHUB_STEP_SUMMARY")
    if summary_file:
        with open(summary_file, "a", encoding="utf-8") as handle:
            handle.write(report)
    for problem in problems:
        log(f"::error::{problem}")
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
