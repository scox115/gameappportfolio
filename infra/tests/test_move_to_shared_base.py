"""Tests for infra/move_to_shared_base.py, with the Azure CLI, sqlpackage and the database reads faked.

    python3 -m unittest discover -s infra/tests
"""
import importlib.util
import io
import os
import sys
import unittest
from contextlib import redirect_stderr
from pathlib import Path
from unittest import mock

SCRIPT = Path(__file__).resolve().parents[1] / "move_to_shared_base.py"
spec = importlib.util.spec_from_file_location("move_to_shared_base", SCRIPT)
move = importlib.util.module_from_spec(spec)
spec.loader.exec_module(move)

OLD_SERVER = "sql-cardarena-abc.database.windows.net"
NEW_SERVER = "sql-portfolio-xyz.database.windows.net"
TARGET = {"group": "rg-portfolio-shared", "environment": "cae-portfolio-xyz", "server": "sql-portfolio-xyz",
          "location": "centralus", "database": "cardarena", "ready": "false"}


def database(migrations=("001_Initial", "002_Shop"), **tables):
    return {"migrations": list(migrations), "tables": tables or {"dbo.Players": 12, "dbo.Battles": 340}}


class FakeAzure:
    """Records az calls and answers the few the move makes."""

    def __init__(self):
        self.calls = []
        self.outputs = {"sqlServer": {"value": OLD_SERVER}, "apiContainerAppName": {"value": "ca-cardarena-api"}}
        self.fail_on = None

    def __call__(self, *args):
        self.calls.append(args)
        command = " ".join(args)
        if self.fail_on and command.startswith(self.fail_on):
            raise RuntimeError(f"az {self.fail_on} failed: Conflict")
        if command.startswith("deployment group list"):
            return self.outputs
        if command.startswith("account get-access-token"):
            return "token"
        if command.startswith("containerapp list"):
            return ["ca-cardarena-api"]
        return None

    def ran(self, prefix):
        return [call for call in self.calls if " ".join(call).startswith(prefix)]

    def option(self, call, name):
        return call[call.index(name) + 1]


class MoveToSharedBaseTests(unittest.TestCase):
    def setUp(self):
        self.azure = FakeAzure()
        self.target = dict(TARGET)
        self.databases = {(OLD_SERVER, "GameDb"): database(), (NEW_SERVER, "cardarena"): database()}
        self.sqlpackage = []
        self.events = []

        def sqlpackage(*args, attempts=3):
            self.sqlpackage.append(args)
            self.events.append(f"sqlpackage {args[0]}")
            if args[0] == "/Action:Export":
                Path(next(a for a in args if a.startswith("/TargetFile:")).split(":", 1)[1]).write_bytes(b"bacpac")

        def az(*args):
            self.events.append(" ".join(args[:3]))
            return self.azure(*args)

        patches = [
            mock.patch.object(move, "az", az),
            mock.patch.object(move, "public_ip", lambda: "20.1.2.3"),
            mock.patch.object(move, "shared_base", lambda group, environment: self.target),
            mock.patch.object(move, "sqlpackage", sqlpackage),
            mock.patch.object(move, "inspect_database", lambda fqdn, name, token: self.databases[(fqdn, name)]),
        ]
        # find_database lives in restore_drill and calls its own az.
        patches.append(mock.patch.object(sys.modules["restore_drill"], "az", az))
        for patch in patches:
            patch.start()
            self.addCleanup(patch.stop)

    def run_move(self, environment="production"):
        with redirect_stderr(io.StringIO()) as errors, mock.patch("sys.stdout", io.StringIO()) as output, \
                mock.patch.dict(os.environ, {}):
            os.environ.pop("GITHUB_STEP_SUMMARY", None)
            code = move.main(["--resource-group", "rg-card-arena", "--shared-resource-group", "rg-portfolio-shared",
                              "--environment", environment])
        return code, output.getvalue(), errors.getvalue()

    def test_copies_the_database_to_a_free_database_on_the_shared_server(self):
        code, report, _ = self.run_move()

        self.assertEqual(code, 0)
        self.assertIn("Production moved to the shared base", report)
        export, import_ = self.sqlpackage
        self.assertIn(f"/SourceServerName:{OLD_SERVER}", export)
        self.assertIn("/SourceDatabaseName:GameDb", export)
        self.assertIn(f"/TargetServerName:{NEW_SERVER}", import_)
        self.assertIn("/TargetDatabaseName:cardarena", import_)
        create = self.azure.ran("deployment group create")[0]
        self.assertEqual(self.azure.option(create, "--resource-group"), "rg-portfolio-shared")
        self.assertEqual(self.azure.option(create, "--name"), "card-arena-database-production")
        self.assertTrue(self.azure.option(create, "--template-file").endswith("modules/database.bicep"))
        self.assertIn("databaseName=cardarena", create)
        self.assertFalse(self.azure.ran("sql db delete"))

    def test_the_old_api_is_taken_down_before_the_export_and_the_new_database_made_after_it(self):
        self.run_move()

        events = self.events
        self.assertLess(events.index("account get-access-token --resource"), events.index("containerapp delete --resource-group"))
        self.assertLess(events.index("containerapp delete --resource-group"), events.index("sqlpackage /Action:Export"))
        self.assertLess(events.index("sqlpackage /Action:Export"), events.index("deployment group create"))
        self.assertLess(events.index("deployment group create"), events.index("sqlpackage /Action:Import"))
        self.assertEqual(self.azure.option(self.azure.ran("containerapp delete")[0], "--name"), "ca-cardarena-api")

    def test_both_firewalls_let_only_this_machine_through_and_only_for_the_move(self):
        self.run_move()

        rules = self.azure.ran("sql server firewall-rule create")
        self.assertEqual([self.azure.option(r, "--server") for r in rules], ["sql-cardarena-abc", "sql-portfolio-xyz"])
        for rule in rules:
            self.assertEqual(self.azure.option(rule, "--start-ip-address"), "20.1.2.3")
        removed = self.azure.ran("sql server firewall-rule delete")
        self.assertEqual([self.azure.option(r, "--server") for r in removed], ["sql-cardarena-abc", "sql-portfolio-xyz"])

    def test_a_copy_that_does_not_match_is_deleted_so_the_game_goes_back(self):
        self.databases[(NEW_SERVER, "cardarena")] = database(**{"dbo.Players": 12, "dbo.Battles": 339})

        code, _, errors = self.run_move()

        self.assertEqual(code, 1)
        self.assertIn("dbo.Battles has 340 rows in the source and 339 in the copy", errors)
        deleted = self.azure.ran("sql db delete")[0]
        self.assertEqual(self.azure.option(deleted, "--name"), "cardarena")
        self.assertEqual(self.azure.option(deleted, "--resource-group"), "rg-portfolio-shared")
        self.assertTrue(self.azure.ran("sql server firewall-rule delete"))

    def test_a_failed_export_leaves_nothing_behind_on_the_shared_server(self):
        def failing(*args, attempts=3):
            raise RuntimeError("sqlpackage /Action:Export failed: timeout")
        with mock.patch.object(move, "sqlpackage", failing):
            code, _, errors = self.run_move()

        self.assertEqual(code, 1)
        self.assertIn("timeout", errors)
        self.assertFalse(self.azure.ran("deployment group create"))
        self.assertFalse(self.azure.ran("sql db delete"))
        self.assertEqual(len(self.azure.ran("sql server firewall-rule delete")), 2)

    def test_the_source_is_never_deleted(self):
        self.databases[(NEW_SERVER, "cardarena")] = database(migrations=())

        self.run_move()

        for call in self.azure.ran("sql db delete"):
            self.assertEqual(self.azure.option(call, "--server"), "sql-portfolio-xyz")

    def test_a_copy_already_moved_is_left_alone(self):
        self.target["ready"] = "true"

        code, report, errors = self.run_move()

        self.assertEqual(code, 0)
        self.assertEqual(report, "")
        self.assertIn("already on the shared base", errors)
        self.assertFalse(self.azure.ran("containerapp delete"))
        self.assertFalse(self.sqlpackage)

    def test_staging_moves_to_its_own_database(self):
        self.target["database"] = "cardarena-staging"
        self.databases[(NEW_SERVER, "cardarena-staging")] = database()

        code, _, _ = self.run_move("staging")

        self.assertEqual(code, 0)
        create = self.azure.ran("deployment group create")[0]
        self.assertEqual(self.azure.option(create, "--name"), "card-arena-database-staging")
        self.assertIn("databaseName=cardarena-staging", create)

    def test_the_comparison_is_exact(self):
        self.assertEqual(move.differences(database(), database()), [])
        self.assertTrue(move.differences(database(), database(migrations=("001_Initial",))))
        self.assertTrue(move.differences(database(), database(**{"dbo.Players": 12})))


if __name__ == "__main__":
    unittest.main()
