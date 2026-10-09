#!/usr/bin/env python3
"""Moves one copy of the game (staging or production) onto the shared portfolio base.

Run once per copy by the "Move to the shared base" workflow, after infra/setup-shared.ps1. It copies
the game's database to its own free database on the shared SQL server and takes the old API down, so
the deploy that follows creates the API again in the shared Container Apps environment. The old
database stays where it is, untouched, until you delete the game's old resources by hand.

    move_to_shared_base.py --resource-group rg-card-arena --shared-resource-group rg-portfolio-shared \\
        --environment production

  1. Read where the database is now (the game's latest deployment) and where it goes (shared-base.sh).
  2. Delete the old API container app. Players see the game as down from here until the deploy
     finishes; it also means nothing writes to the database while it is copied. (Its environment can't
     be changed, and the new one has the same name, so it can't simply be redeployed.)
  3. Export the database to a BACPAC file with sqlpackage, create the new free database on the shared
     server from infra/modules/database.bicep, and import the file into it.
  4. Check that the copy has the same migrations and the same row count in every table.

If anything fails after the new database was created, it is deleted again, so the next deploy puts the
game back where it was. Needs the Azure CLI signed in as an admin of both SQL servers (the deploy app),
the .NET SDK and sqlpackage. See docs/adr/0040-shared-portfolio-base.md.
"""
import argparse
import json
import os
import subprocess
import sys
import tempfile
import time
from pathlib import Path

INFRA = Path(__file__).resolve().parent
sys.path.insert(0, str(INFRA / "restore-drill"))
from restore_drill import SQL_RESOURCE, az, find_database, inspect_database, log, public_ip  # noqa: E402

DATABASE_TEMPLATE = INFRA / "modules" / "database.bicep"
TAGS = {"app": "kings-of-the-card-arena"}


def shared_base(group, environment):
    """shared-base.sh's answer: the shared group, environment, server, location, database and ready."""
    result = subprocess.run(["bash", str(INFRA / "shared-base.sh"), group, environment], capture_output=True, text=True)
    if result.returncode != 0:
        raise RuntimeError(result.stderr.strip().removeprefix("::error::") or "shared-base.sh failed.")
    return dict(line.split("=", 1) for line in result.stdout.splitlines() if "=" in line)


def sqlpackage(*args, attempts=3):
    """Runs sqlpackage, trying again after a pause: a paused serverless database can refuse the first
    connection while it wakes up."""
    for attempt in range(1, attempts + 1):
        result = subprocess.run(["sqlpackage", *args], capture_output=True, text=True)
        if result.returncode == 0:
            return
        detail = (result.stderr.strip() or result.stdout.strip()).splitlines()[-5:]
        if attempt == attempts:
            raise RuntimeError(f"sqlpackage {args[0]} failed: {' '.join(detail)}")
        log(f"sqlpackage {args[0]} failed (attempt {attempt} of {attempts}), trying again in a minute: {' '.join(detail)}")
        time.sleep(60)


def differences(source, copy):
    """What the copy lacks; an empty list means it matches. Nothing writes to the database during the
    move, so the counts must match exactly."""
    problems = []
    if copy["migrations"] != source["migrations"]:
        problems.append(f"The copy is on migration {(copy['migrations'] or ['none'])[-1]}, "
                        f"the source on {(source['migrations'] or ['none'])[-1]}.")
    for table in sorted(set(source["tables"]) | set(copy["tables"])):
        if source["tables"].get(table) != copy["tables"].get(table):
            problems.append(f"{table} has {source['tables'].get(table, 'no')} rows in the source "
                            f"and {copy['tables'].get(table, 'no')} in the copy.")
    return problems


def report(environment, old_server, old_database, target, source, seconds):
    lines = [
        f"### ✅ {environment.capitalize()} moved to the shared base",
        "",
        f"- Copied `{old_database}` on `{old_server}` to `{target['database']}` on `{target['server']}` "
        f"in **{seconds / 60:.1f} minutes**.",
        f"- {len(source['tables'])} tables, {sum(source['tables'].values())} rows, "
        f"migration `{(source['migrations'] or ['none'])[-1]}`; the copy matches.",
        f"- The old database is untouched. Delete the old resources once {environment} works on the shared base.",
        "",
    ]
    return "\n".join(lines)


def run(group, shared_group, environment):
    fqdn, source_group, source_database = find_database(group)
    target = shared_base(shared_group, environment)
    if source_group == shared_group or target.get("ready") == "true":
        log(f"{environment.capitalize()} is already on the shared base ({target.get('database')} on "
            f"{target.get('server')}); nothing to move.")
        return None
    source_server = fqdn.split(".")[0]
    target_fqdn = f"{target['server']}.database.windows.net"

    stamp = time.strftime("%Y%m%d%H%M", time.gmtime())
    rule = f"move-to-shared-{stamp}"
    ip = public_ip()
    servers = [(source_group, source_server), (shared_group, target["server"])]
    opened = []
    created = False
    clock = time.monotonic()
    try:
        for rule_group, server in servers:
            log(f"Letting {ip} through the firewall of {server}.")
            az("sql", "server", "firewall-rule", "create", "--resource-group", rule_group, "--server", server,
               "--name", rule, "--start-ip-address", ip, "--end-ip-address", ip)
            opened.append((rule_group, server))

        # In GitHub Actions the CLI signs in with an OIDC assertion that expires 5 minutes after sign-in,
        # so get the database token (good for about an hour) now, before the long export. Reading the
        # source first also wakes it up and proves the sign-in works before anything is taken down.
        token = az("account", "get-access-token", "--resource", SQL_RESOURCE, "--query", "accessToken")
        before = inspect_database(fqdn, source_database, token)
        log(f"The source has {len(before['tables'])} tables and {sum(before['tables'].values())} rows.")

        apps = az("containerapp", "list", "--resource-group", group, "--query", "[?ends_with(name, '-api')].name") or []
        for app in apps:
            log(f"Deleting the old API container app {app}; the game is down until the deploy finishes.")
            az("containerapp", "delete", "--resource-group", group, "--name", app, "--yes")
        # Read again now nothing writes to it, so the comparison below is exact.
        source = inspect_database(fqdn, source_database, token)

        with tempfile.TemporaryDirectory() as folder:
            bacpac = os.path.join(folder, f"{source_database}.bacpac")
            log(f"Exporting {source_database} from {source_server}...")
            sqlpackage("/Action:Export", f"/SourceServerName:{fqdn}", f"/SourceDatabaseName:{source_database}",
                       f"/AccessToken:{token}", f"/TargetFile:{bacpac}")
            log(f"Exported {os.path.getsize(bacpac) / 1e6:.1f} MB. Creating {target['database']} on {target['server']}...")

            created = True
            az("deployment", "group", "create", "--resource-group", shared_group,
               "--name", f"card-arena-database-{environment}", "--template-file", str(DATABASE_TEMPLATE),
               "--parameters", f"serverName={target['server']}", f"databaseName={target['database']}",
               f"location={target['location']}", f"tags={json.dumps({**TAGS, 'environment': environment})}")
            log(f"Importing into {target['database']}...")
            sqlpackage("/Action:Import", f"/TargetServerName:{target_fqdn}", f"/TargetDatabaseName:{target['database']}",
                       f"/AccessToken:{token}", f"/SourceFile:{bacpac}")

        problems = differences(source, inspect_database(target_fqdn, target["database"], token))
        if problems:
            raise RuntimeError("The copy doesn't match the source: " + " ".join(problems))
        created = False  # it's good: keep it
        return report(environment, source_server, source_database, target, source, time.monotonic() - clock)
    finally:
        if created:
            log(f"Deleting {target['database']}, so the next deploy puts the game back where it was.")
            try:
                az("sql", "db", "delete", "--resource-group", shared_group, "--server", target["server"],
                   "--name", target["database"], "--yes")
            except RuntimeError as error:
                log(f"::warning::Couldn't delete {target['database']}; delete it in the portal before deploying. {error}")
        for rule_group, server in opened:
            try:
                az("sql", "server", "firewall-rule", "delete", "--resource-group", rule_group, "--server", server,
                   "--name", rule)
            except RuntimeError as error:
                log(f"::warning::Couldn't remove the firewall rule {rule} on {server}; remove it in the portal. {error}")


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--resource-group", required=True, help="the game's resource group for this environment")
    parser.add_argument("--shared-resource-group", required=True)
    parser.add_argument("--environment", required=True, choices=["staging", "production"])
    args = parser.parse_args(argv)

    try:
        summary = run(args.resource_group, args.shared_resource_group, args.environment)
    except RuntimeError as error:
        log(f"::error::{error}")
        return 1
    if summary:
        print(summary)
        summary_file = os.environ.get("GITHUB_STEP_SUMMARY")
        if summary_file:
            with open(summary_file, "a", encoding="utf-8") as handle:
                handle.write(summary)
    return 0


if __name__ == "__main__":
    sys.exit(main())
